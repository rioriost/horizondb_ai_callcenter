using System.Buffers;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(AppDatabaseOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<AiModelOptions>(_ => AiModelOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<SpeechTranscriptionOptions>(_ => SpeechTranscriptionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<AzureSpeechTokenProvider>();
builder.Services.AddHttpClient<AzureOpenAiClient>();
builder.Services.AddSingleton<ConversationRepository>();
builder.Services.AddSingleton<ConversationService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
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

app.Map("/ws/audio/conversations/{conversationId:guid}", async (
    Guid conversationId,
    HttpContext context,
    ConversationService service,
    SpeechTranscriptionOptions speechOptions,
    AzureSpeechTokenProvider speechTokenProvider,
    CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket request expected.", cancellationToken);
        return;
    }

    speechOptions.EnsureConfigured();

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await RunAudioTranscriptionSessionAsync(socket, conversationId, service, speechOptions, speechTokenProvider, cancellationToken);
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

static async Task RunAudioTranscriptionSessionAsync(
    WebSocket socket,
    Guid conversationId,
    ConversationService service,
    SpeechTranscriptionOptions speechOptions,
    AzureSpeechTokenProvider speechTokenProvider,
    CancellationToken cancellationToken)
{
    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    var sendLock = new SemaphoreSlim(1, 1);
    var sessionStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sequenceNo = 0;
    var started = false;
    var sampleRate = speechOptions.SampleRate;

    PushAudioInputStream? pushStream = null;
    AudioConfig? audioConfig = null;
    SpeechRecognizer? recognizer = null;

    async Task SafeSendAsync(object value)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await SendJsonAsync(socket, value, cancellationToken);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    async Task StartSpeechAsync()
    {
        if (started)
        {
            return;
        }

        var token = await speechTokenProvider.GetAccessTokenAsync(cancellationToken);
        var speechConfig = SpeechConfig.FromAuthorizationToken(token.Token, speechOptions.Region);
        speechConfig.SpeechRecognitionLanguage = speechOptions.Language;

        var audioFormat = AudioStreamFormat.GetWaveFormatPCM((uint)sampleRate, 16, 1);
        pushStream = AudioInputStream.CreatePushStream(audioFormat);
        audioConfig = AudioConfig.FromStreamInput(pushStream);
        recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        recognizer.Recognizing += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Result.Text))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var request = new TranscriptChunkRequest(sequenceNo, eventArgs.Result.Text, IsFinal: false);
                    var result = await service.AcceptTranscriptAsync(conversationId, request, cancellationToken);
                    await SafeSendAsync(new AudioTranscriptionMessage("partial", sequenceNo, eventArgs.Result.Text, result, null, null));
                }
                catch (Exception ex)
                {
                    await SafeSendAsync(new AudioTranscriptionMessage("error", sequenceNo, null, null, "partial_failed", ex.Message));
                }
            }, cancellationToken);
        };

        recognizer.Recognized += (_, eventArgs) =>
        {
            if (eventArgs.Result.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(eventArgs.Result.Text))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                var currentSequence = Interlocked.Increment(ref sequenceNo) - 1;
                try
                {
                    var request = new TranscriptChunkRequest(currentSequence, eventArgs.Result.Text, IsFinal: true);
                    var result = await service.AcceptTranscriptAsync(conversationId, request, cancellationToken);
                    await SafeSendAsync(new AudioTranscriptionMessage("final", currentSequence, eventArgs.Result.Text, result, null, null));
                    if (result.Response is not null)
                    {
                        await SafeSendAsync(new AudioTranscriptionMessage("response", currentSequence, result.Response.ResponseText, result, null, null));
                    }
                }
                catch (Exception ex)
                {
                    await SafeSendAsync(new AudioTranscriptionMessage("error", currentSequence, eventArgs.Result.Text, null, "final_failed", ex.Message));
                }
            }, cancellationToken);
        };

        recognizer.Canceled += (_, eventArgs) =>
        {
            _ = SafeSendAsync(new AudioTranscriptionMessage("error", null, null, null, "speech_canceled", $"{eventArgs.Reason}: {eventArgs.ErrorDetails}"));
            sessionStopped.TrySetResult();
        };
        recognizer.SessionStopped += (_, _) => sessionStopped.TrySetResult();

        await recognizer.StartContinuousRecognitionAsync();
        started = true;
        await SafeSendAsync(new AudioTranscriptionMessage("ready", null, null, null, null, null));
    }

    try
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var controlText = await ReceiveRemainingTextAsync(socket, buffer, result, cancellationToken);
                var control = JsonSerializer.Deserialize<AudioControlMessage>(controlText, JsonOptions.Default);
                if (control?.Type.Equals("stop", StringComparison.OrdinalIgnoreCase) == true)
                {
                    break;
                }

                if (control?.SampleRate is > 0)
                {
                    sampleRate = control.SampleRate.Value;
                }

                await StartSpeechAsync();
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                await SafeSendAsync(new AudioTranscriptionMessage("error", null, null, null, "unsupported_message", "Only JSON control messages and PCM audio binary messages are supported."));
                continue;
            }

            await StartSpeechAsync();
            pushStream?.Write(buffer.AsSpan(0, result.Count).ToArray());
        }
    }
    finally
    {
        pushStream?.Close();
        if (recognizer is not null)
        {
            await Task.WhenAny(sessionStopped.Task, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            await recognizer.StopContinuousRecognitionAsync();
            recognizer.Dispose();
        }

        audioConfig?.Dispose();
        pushStream?.Dispose();
        ArrayPool<byte>.Shared.Return(buffer);

        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Audio session ended.", CancellationToken.None);
        }
    }
}

static async Task<string> ReceiveRemainingTextAsync(
    WebSocket socket,
    byte[] buffer,
    WebSocketReceiveResult firstResult,
    CancellationToken cancellationToken)
{
    using var body = new MemoryStream();
    body.Write(buffer, 0, firstResult.Count);

    var result = firstResult;
    while (!result.EndOfMessage)
    {
        result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidOperationException("Fragmented control messages must remain text messages.");
        }

        body.Write(buffer, 0, result.Count);
    }

    return Encoding.UTF8.GetString(body.ToArray());
}

sealed class ConversationService(ConversationRepository repository, AzureOpenAiClient aiClient)
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

        var embedding = await aiClient.CreateEmbeddingAsync(request.Text, cancellationToken);
        await repository.FinalizeTranscriptAsync(conversationId, request.SequenceNo, request.Text, embedding, cancellationToken);

        var candidates = await repository.GetResponseCandidatesAsync(embedding, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No enabled response_master rows were found. Seed response data before using the call center API.");
        }

        var response = await aiClient.RerankAsync(request.Text, candidates, cancellationToken);
        await repository.RecordResponseAsync(conversationId, request.SequenceNo, response, cancellationToken);

        return new TranscriptAcceptedResponse(conversationId, request.SequenceNo, "responded", response);
    }
}

sealed class ConversationRepository(AppDatabaseOptions databaseOptions)
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

    public async Task FinalizeTranscriptAsync(
        Guid conversationId,
        int sequenceNo,
        string text,
        float[] embedding,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpsertFinalTranscriptAsync(connection, null, conversationId, sequenceNo, text, embedding, cancellationToken);
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
        NpgsqlTransaction? transaction,
        Guid conversationId,
        int sequenceNo,
        string text,
        float[] embedding,
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
                @embedding::vector,
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
        command.Parameters.AddWithValue("embedding", ToVectorLiteral(embedding));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<ResponseCandidate>> GetResponseCandidatesAsync(float[] embedding, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                response_text,
                (embedding <=> @embedding::vector)::double precision AS distance
            FROM response_master
            WHERE enabled = true AND embedding IS NOT NULL
            ORDER BY embedding <=> @embedding::vector
            LIMIT @candidate_limit;
            """;
        command.Parameters.AddWithValue("embedding", ToVectorLiteral(embedding));
        command.Parameters.AddWithValue("candidate_limit", 20);

        var candidates = new List<ResponseCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new ResponseCandidate(reader.GetGuid(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return candidates;
    }

    public async Task RecordResponseAsync(
        Guid conversationId,
        int sequenceNo,
        SelectedResponse response,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertResponseEventAsync(connection, transaction, conversationId, sequenceNo, response, cancellationToken);
        await MarkRespondedAsync(connection, transaction, conversationId, sequenceNo, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    static string ToVectorLiteral(float[] embedding)
    {
        return "[" + string.Join(",", embedding.Select(static value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
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

sealed class AzureOpenAiClient(HttpClient httpClient, AiModelOptions options)
{
    readonly TokenCredential credential = new DefaultAzureCredential();

    public async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        options.EnsureConfigured();
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"openai/deployments/{Uri.EscapeDataString(options.EmbeddingDeployment)}/embeddings?api-version={Uri.EscapeDataString(options.ApiVersion)}",
            new { input = text },
            cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure OpenAI embedding request failed: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data")[0].GetProperty("embedding")
            .EnumerateArray()
            .Select(static item => item.GetSingle())
            .ToArray();
    }

    public async Task<SelectedResponse> RerankAsync(string query, IReadOnlyList<ResponseCandidate> candidates, CancellationToken cancellationToken)
    {
        options.EnsureConfigured();
        var candidateText = string.Join("\n", candidates.Select((candidate, index) =>
            $"{index + 1}. id={candidate.ResponseId} distance={candidate.Distance:R} text={candidate.ResponseText}"));

        var prompt = $"""
            Select the best response for the Japanese call-center customer utterance.
            Return only the response id UUID. Do not return explanations.

            Customer utterance:
            {query}

            Candidate responses:
            {candidateText}
            """;

        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"openai/deployments/{Uri.EscapeDataString(options.ChatDeployment)}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}",
            new
            {
                messages = new object[]
                {
                    new { role = "system", content = "You are a precise reranker for call-center response candidates." },
                    new { role = "user", content = prompt }
                },
                max_tokens = 64
            },
            cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure OpenAI rerank request failed: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var selected = candidates.FirstOrDefault(candidate => content.Contains(candidate.ResponseId.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];

        return new SelectedResponse(selected.ResponseId, selected.ResponseText, selected.Distance, 1.0);
    }

    async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string relativePath, object payload, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);

        var request = new HttpRequestMessage(method, new Uri(options.Endpoint, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Default), Encoding.UTF8, "application/json");
        return request;
    }
}

sealed record AiModelOptions(Uri Endpoint, string ApiVersion, string EmbeddingDeployment, string ChatDeployment)
{
    public void EnsureConfigured()
    {
        if (Endpoint == EmptyEndpoint)
        {
            throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required.");
        }
    }

    static readonly Uri EmptyEndpoint = new("https://localhost/");

    public static AiModelOptions FromConfiguration(IConfiguration configuration)
    {
        var endpointText = configuration["AZURE_OPENAI_ENDPOINT"];
        return new AiModelOptions(
            Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ? endpoint : EmptyEndpoint,
            configuration["AZURE_OPENAI_API_VERSION"] ?? "2024-10-21",
            configuration["AZURE_OPENAI_EMBED_DEPLOYMENT"] ?? "app-embedding",
            configuration["AZURE_OPENAI_CHAT_DEPLOYMENT"] ?? "app-reranker");
    }
}

sealed class AzureSpeechTokenProvider
{
    readonly TokenCredential credential = new DefaultAzureCredential();

    public async Task<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        return await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);
    }
}

sealed record SpeechTranscriptionOptions(string? Region, string Language, int SampleRate)
{
    public void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(Region))
        {
            throw new InvalidOperationException("SPEECH_REGION is required for browser audio transcription.");
        }
    }

    public static SpeechTranscriptionOptions FromConfiguration(IConfiguration configuration)
    {
        var sampleRate = int.TryParse(configuration["SPEECH_AUDIO_SAMPLE_RATE"], out var configuredSampleRate)
            ? configuredSampleRate
            : 16000;

        return new SpeechTranscriptionOptions(
            configuration["SPEECH_REGION"],
            configuration["SPEECH_RECOGNITION_LANGUAGE"] ?? "ja-JP",
            sampleRate);
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
sealed record ResponseCandidate(Guid ResponseId, string ResponseText, double Distance);
sealed record ErrorResponse(string Code, string Message);
sealed record AudioControlMessage(string Type, int? SampleRate);
sealed record AudioTranscriptionMessage(string Type, int? SequenceNo, string? Text, TranscriptAcceptedResponse? Result, string? Code, string? Message);
