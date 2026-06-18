using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(AppDatabaseOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ResponseSelectionOptions>(_ => ResponseSelectionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ConversationRepository>();
builder.Services.AddSingleton<ConversationService>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok", DateTimeOffset.UtcNow)));

app.MapPost("/api/conversations", () =>
{
    var conversationId = Guid.NewGuid();
    return Results.Created($"/api/conversations/{conversationId}", new ConversationCreatedResponse(conversationId));
});

app.MapPost("/api/conversations/{conversationId:guid}/transcript", async (
    Guid conversationId,
    TranscriptChunkRequest request,
    ConversationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.AcceptTranscriptAsync(conversationId, request, cancellationToken);
    return result.Response is null
        ? Results.Accepted(value: result)
        : Results.Ok(result);
});

app.MapPost("/api/conversations/{conversationId:guid}/respond", async (
    Guid conversationId,
    RespondRequest request,
    ConversationService service,
    CancellationToken cancellationToken) =>
{
    var chunk = new TranscriptChunkRequest(request.SequenceNo, request.Text, IsFinal: true);
    var result = await service.AcceptTranscriptAsync(conversationId, chunk, cancellationToken);
    return Results.Ok(result);
});

app.Map("/ws/conversations/{conversationId:guid}", async (
    Guid conversationId,
    HttpContext context,
    ConversationService service,
    CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket request expected.", cancellationToken);
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

    try
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveTextMessageAsync(socket, buffer, cancellationToken);
            if (payload is null)
            {
                break;
            }

            TranscriptChunkRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<TranscriptChunkRequest>(payload, JsonOptions.Default);
            }
            catch (JsonException ex)
            {
                await SendJsonAsync(socket, new ErrorResponse("invalid_json", ex.Message), cancellationToken);
                continue;
            }

            if (request is null)
            {
                await SendJsonAsync(socket, new ErrorResponse("invalid_payload", "Transcript payload is required."), cancellationToken);
                continue;
            }

            var result = await service.AcceptTranscriptAsync(conversationId, request, cancellationToken);
            await SendJsonAsync(socket, result, cancellationToken);
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
});

app.Run();

static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
{
    using var body = new MemoryStream();

    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
            return null;
        }

        if (result.MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidOperationException("Only text WebSocket messages are supported.");
        }

        body.Write(buffer, 0, result.Count);

        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(body.ToArray());
        }
    }
}

static async Task SendJsonAsync(WebSocket socket, object value, CancellationToken cancellationToken)
{
    var payload = JsonSerializer.Serialize(value, JsonOptions.Default);
    var bytes = Encoding.UTF8.GetBytes(payload);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}

sealed class ConversationService(ConversationRepository repository)
{
    public async Task<TranscriptAcceptedResponse> AcceptTranscriptAsync(
        Guid conversationId,
        TranscriptChunkRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new BadHttpRequestException("Transcript text is required.");
        }

        if (request.SequenceNo < 0)
        {
            throw new BadHttpRequestException("sequenceNo must be zero or greater.");
        }

        if (!request.IsFinal)
        {
            await repository.UpsertPartialTranscriptAsync(conversationId, request.SequenceNo, request.Text, cancellationToken);
            return new TranscriptAcceptedResponse(conversationId, request.SequenceNo, "streaming", null);
        }

        var response = await repository.FinalizeTranscriptAndSelectResponseAsync(
            conversationId,
            request.SequenceNo,
            request.Text,
            cancellationToken);

        return new TranscriptAcceptedResponse(conversationId, request.SequenceNo, "responded", response);
    }
}

sealed class ConversationRepository(AppDatabaseOptions databaseOptions, ResponseSelectionOptions selectionOptions)
{
    public async Task UpsertPartialTranscriptAsync(Guid conversationId, int sequenceNo, string text, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_segments (conversation_id, sequence_no, partial_text, status, updated_at)
            VALUES (@conversation_id, @sequence_no, @partial_text, 'streaming', now())
            ON CONFLICT (conversation_id, sequence_no)
            DO UPDATE SET partial_text = EXCLUDED.partial_text, status = 'streaming', updated_at = now();
            """;
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("sequence_no", sequenceNo);
        command.Parameters.AddWithValue("partial_text", text);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SelectedResponse> FinalizeTranscriptAndSelectResponseAsync(
        Guid conversationId,
        int sequenceNo,
        string text,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertFinalTranscriptAsync(connection, transaction, conversationId, sequenceNo, text, cancellationToken);

        var response = await SelectResponseAsync(connection, transaction, conversationId, sequenceNo, text, cancellationToken);
        if (response is null)
        {
            throw new InvalidOperationException("No enabled response_master rows were found. Seed response data before using the call center API.");
        }

        await InsertResponseEventAsync(connection, transaction, conversationId, sequenceNo, response, cancellationToken);
        await MarkRespondedAsync(connection, transaction, conversationId, sequenceNo, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (!databaseOptions.IsConfigured)
        {
            throw new InvalidOperationException(databaseOptions.ConfigurationError);
        }

        var connection = new NpgsqlConnection(databaseOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    async Task UpsertFinalTranscriptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid conversationId,
        int sequenceNo,
        string text,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_segments (conversation_id, sequence_no, partial_text, final_text, embedding, status, updated_at)
            VALUES (
                @conversation_id,
                @sequence_no,
                @text,
                @text,
                azure_openai.create_embeddings(@embedding_model, @text)::vector,
                'finalized',
                now()
            )
            ON CONFLICT (conversation_id, sequence_no)
            DO UPDATE SET
                partial_text = EXCLUDED.partial_text,
                final_text = EXCLUDED.final_text,
                embedding = EXCLUDED.embedding,
                status = 'finalized',
                updated_at = now();
            """;
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("sequence_no", sequenceNo);
        command.Parameters.AddWithValue("text", text);
        command.Parameters.AddWithValue("embedding_model", selectionOptions.EmbeddingModelAlias);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<SelectedResponse?> SelectResponseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid conversationId,
        int sequenceNo,
        string text,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH query_segment AS (
                SELECT embedding
                FROM conversation_segments
                WHERE conversation_id = @conversation_id AND sequence_no = @sequence_no
            ),
            candidates AS (
                SELECT
                    rm.id,
                    rm.response_text,
                    (rm.embedding <=> qs.embedding)::double precision AS distance
                FROM response_master rm
                CROSS JOIN query_segment qs
                WHERE rm.enabled = true
                ORDER BY rm.embedding <=> qs.embedding
                LIMIT @candidate_limit
            ),
            reranked AS (
                SELECT document_id, rank, relevance_score
                FROM azure_ai.rank(
                    @query,
                    ARRAY(SELECT response_text FROM candidates),
                    ARRAY(SELECT id::text FROM candidates),
                    @reranker_model
                )
            )
            SELECT
                c.id,
                c.response_text,
                c.distance,
                r.relevance_score::double precision
            FROM candidates c
            JOIN reranked r ON r.document_id = c.id::text
            ORDER BY r.rank ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("sequence_no", sequenceNo);
        command.Parameters.AddWithValue("candidate_limit", selectionOptions.CandidateLimit);
        command.Parameters.AddWithValue("query", text);
        command.Parameters.AddWithValue("reranker_model", selectionOptions.RerankerModelAlias);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SelectedResponse(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.GetDouble(3));
    }

    static async Task InsertResponseEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid conversationId,
        int sequenceNo,
        SelectedResponse response,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO response_events (
                conversation_id,
                sequence_no,
                selected_response_id,
                distance,
                rerank_score,
                spoken_text
            )
            VALUES (
                @conversation_id,
                @sequence_no,
                @selected_response_id,
                @distance,
                @rerank_score,
                @spoken_text
            );
            """;
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("sequence_no", sequenceNo);
        command.Parameters.AddWithValue("selected_response_id", response.ResponseId);
        command.Parameters.AddWithValue("distance", response.Distance);
        command.Parameters.AddWithValue("rerank_score", response.RerankScore);
        command.Parameters.AddWithValue("spoken_text", response.ResponseText);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task MarkRespondedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid conversationId,
        int sequenceNo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversation_segments
            SET status = 'responded', updated_at = now()
            WHERE conversation_id = @conversation_id AND sequence_no = @sequence_no;
            """;
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("sequence_no", sequenceNo);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

sealed record AppDatabaseOptions(string? ConnectionString, string? ConfigurationError)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    public static AppDatabaseOptions FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("CallCenter");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new AppDatabaseOptions(configured, null);
        }

        var host = configuration["POSTGRES_HOST"];
        var database = configuration["POSTGRES_DATABASE"] ?? configuration["POSTGRES_NAME"];
        var user = configuration["POSTGRES_USERNAME"] ?? configuration["DB_USER"];
        var password = configuration["POSTGRES_PASSWORD"] ?? configuration["DB_PASSWORD"];

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(host)) missing.Add("POSTGRES_HOST");
        if (string.IsNullOrWhiteSpace(database)) missing.Add("POSTGRES_DATABASE");
        if (string.IsNullOrWhiteSpace(user)) missing.Add("POSTGRES_USERNAME or DB_USER");
        if (string.IsNullOrWhiteSpace(password)) missing.Add("POSTGRES_PASSWORD or DB_PASSWORD");

        if (missing.Count > 0)
        {
            return new AppDatabaseOptions(null, $"Database configuration is incomplete. Missing: {string.Join(", ", missing)}.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = database,
            Username = user,
            Password = password,
            Port = int.TryParse(configuration["POSTGRES_PORT"], out var port) ? port : 5432,
            SslMode = SslMode.Require,
            Timeout = 15
        };

        return new AppDatabaseOptions(builder.ConnectionString, null);
    }
}

sealed record ResponseSelectionOptions(string EmbeddingModelAlias, string RerankerModelAlias, int CandidateLimit)
{
    public static ResponseSelectionOptions FromConfiguration(IConfiguration configuration)
    {
        return new ResponseSelectionOptions(
            configuration["AI_EMBEDDING_MODEL_ALIAS"] ?? "app-embedding",
            configuration["AI_RERANKER_MODEL_ALIAS"] ?? "app-reranker",
            int.TryParse(configuration["RESPONSE_CANDIDATE_LIMIT"], out var limit) ? Math.Clamp(limit, 1, 100) : 20);
    }
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}

sealed record HealthResponse(string Status, DateTimeOffset Timestamp);
sealed record ConversationCreatedResponse(Guid ConversationId);
sealed record TranscriptChunkRequest(int SequenceNo, string Text, bool IsFinal);
sealed record RespondRequest(int SequenceNo, string Text);
sealed record TranscriptAcceptedResponse(Guid ConversationId, int SequenceNo, string Status, SelectedResponse? Response);
sealed record SelectedResponse(Guid ResponseId, string ResponseText, double Distance, double RerankScore);
sealed record ErrorResponse(string Code, string Message);
