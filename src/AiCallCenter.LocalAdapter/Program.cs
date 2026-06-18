using System.Net.Http.Json;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

var options = AdapterOptions.FromEnvironment(args);
using var http = new HttpClient { BaseAddress = options.ApiBaseUrl };

Console.WriteLine($"Connecting to API: {options.ApiBaseUrl}");
var conversation = await http.PostAsJsonAsync("/api/conversations", new { });
await EnsureSuccessAsync(conversation, "create conversation");

var created = await conversation.Content.ReadFromJsonAsync<ConversationCreatedResponse>();
if (created is null || created.ConversationId == Guid.Empty)
{
    throw new InvalidOperationException("API did not return a conversationId.");
}

Console.WriteLine($"Conversation: {created.ConversationId}");

if (!string.IsNullOrWhiteSpace(options.OneShotText))
{
    var response = await SendTranscriptAsync(http, created.ConversationId, 0, options.OneShotText, true);
    await SpeakIfConfiguredAsync(options, response?.Response?.ResponseText);
    return;
}

options.EnsureSpeechConfigured();

var speechConfig = SpeechConfig.FromSubscription(options.SpeechKey, options.SpeechRegion);
speechConfig.SpeechRecognitionLanguage = options.SpeechRecognitionLanguage;
speechConfig.SpeechSynthesisVoiceName = options.SpeechVoiceName;

using var audioConfig = AudioConfig.FromDefaultMicrophoneInput();
using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);
var sequenceNo = 0;
var sendLock = new SemaphoreSlim(1, 1);
var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.TrySetResult();
};

recognizer.Recognizing += async (_, eventArgs) =>
{
    if (string.IsNullOrWhiteSpace(eventArgs.Result.Text))
    {
        return;
    }

    await sendLock.WaitAsync();
    try
    {
        await SendTranscriptAsync(http, created.ConversationId, sequenceNo, eventArgs.Result.Text, isFinal: false);
        Console.Write($"\r{eventArgs.Result.Text}   ");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\nFailed to send partial transcript: {ex.Message}");
    }
    finally
    {
        sendLock.Release();
    }
};

recognizer.Recognized += async (_, eventArgs) =>
{
    if (eventArgs.Result.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(eventArgs.Result.Text))
    {
        return;
    }

    await sendLock.WaitAsync();
    try
    {
        var currentSequence = sequenceNo++;
        Console.WriteLine($"\n[{currentSequence}] {eventArgs.Result.Text}");
        var apiResponse = await SendTranscriptAsync(http, created.ConversationId, currentSequence, eventArgs.Result.Text, isFinal: true);
        await SpeakIfConfiguredAsync(options, apiResponse?.Response?.ResponseText);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to process final transcript: {ex.Message}");
    }
    finally
    {
        sendLock.Release();
    }
};

recognizer.Canceled += (_, eventArgs) =>
{
    Console.Error.WriteLine($"Speech recognition canceled: {eventArgs.Reason} {eventArgs.ErrorDetails}");
    stop.TrySetResult();
};

recognizer.SessionStopped += (_, _) => stop.TrySetResult();

Console.WriteLine("Listening. Press Ctrl+C to stop.");
await recognizer.StartContinuousRecognitionAsync();
await stop.Task;
await recognizer.StopContinuousRecognitionAsync();

static async Task<TranscriptAcceptedResponse?> SendTranscriptAsync(
    HttpClient http,
    Guid conversationId,
    int sequenceNo,
    string text,
    bool isFinal)
{
    var payload = new TranscriptChunkRequest(sequenceNo, text, isFinal);
    using var response = await http.PostAsJsonAsync($"/api/conversations/{conversationId}/transcript", payload);
    await EnsureSuccessAsync(response, "send transcript");

    var accepted = await response.Content.ReadFromJsonAsync<TranscriptAcceptedResponse>();
    if (accepted?.Response is not null)
    {
        Console.WriteLine($"Response: {accepted.Response.ResponseText}");
    }

    return accepted;
}

static async Task SpeakIfConfiguredAsync(AdapterOptions options, string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return;
    }

    if (!options.HasSpeechConfiguration)
    {
        Console.WriteLine($"TTS skipped because SPEECH_KEY or SPEECH_REGION is not configured: {text}");
        return;
    }

    var speechConfig = SpeechConfig.FromSubscription(options.SpeechKey, options.SpeechRegion);
    speechConfig.SpeechSynthesisVoiceName = options.SpeechVoiceName;
    using var synthesizer = new SpeechSynthesizer(speechConfig);
    var result = await synthesizer.SpeakTextAsync(text);
    if (result.Reason != ResultReason.SynthesizingAudioCompleted)
    {
        throw new InvalidOperationException($"Speech synthesis failed: {result.Reason}");
    }
}

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
    {
        return;
    }

    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"Failed to {operation}: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
}

sealed record AdapterOptions(
    Uri ApiBaseUrl,
    string? SpeechKey,
    string? SpeechRegion,
    string SpeechRecognitionLanguage,
    string SpeechVoiceName,
    string? OneShotText)
{
    public bool HasSpeechConfiguration => !string.IsNullOrWhiteSpace(SpeechKey) && !string.IsNullOrWhiteSpace(SpeechRegion);

    public void EnsureSpeechConfigured()
    {
        if (!HasSpeechConfiguration)
        {
            throw new InvalidOperationException("SPEECH_KEY and SPEECH_REGION are required for microphone mode. Use --text \"...\" for API-only smoke testing.");
        }
    }

    public static AdapterOptions FromEnvironment(string[] args)
    {
        var apiBase = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:8080";
        var oneShotText = ReadOption(args, "--text");

        return new AdapterOptions(
            new Uri(apiBase.TrimEnd('/')),
            Environment.GetEnvironmentVariable("SPEECH_KEY"),
            Environment.GetEnvironmentVariable("SPEECH_REGION"),
            Environment.GetEnvironmentVariable("SPEECH_RECOGNITION_LANGUAGE") ?? "ja-JP",
            Environment.GetEnvironmentVariable("SPEECH_VOICE_NAME") ?? "ja-JP-NanamiNeural",
            oneShotText);
    }

    static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

sealed record ConversationCreatedResponse(Guid ConversationId);
sealed record TranscriptChunkRequest(int SequenceNo, string Text, bool IsFinal);
sealed record TranscriptAcceptedResponse(Guid ConversationId, int SequenceNo, string Status, SelectedResponse? Response);
sealed record SelectedResponse(Guid ResponseId, string ResponseText, double Distance, double RerankScore);
