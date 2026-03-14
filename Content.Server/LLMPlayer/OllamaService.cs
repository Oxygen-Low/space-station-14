using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;

namespace Content.Server.LLMPlayer;

/// <summary>
///     Service for communicating with the Ollama API to get LLM responses.
/// </summary>
public sealed class OllamaService
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly ISawmill _sawmill = Logger.GetSawmill("llm-player");

    private string _ollamaUrl = "http://localhost:11434";
    private string _model = "gemma3:12b";

    public void Initialize()
    {
        IoCManager.InjectDependencies(this);
        _cfg.OnValueChanged(CCVars.LLMPlayerOllamaUrl, value => _ollamaUrl = value, true);
        _cfg.OnValueChanged(CCVars.LLMPlayerModel, value => _model = value, true);
    }

    /// <summary>
    ///     Sends a chat completion request to the Ollama API and returns the response text.
    /// </summary>
    public async Task<string?> ChatAsync(List<LLMMessage> messages)
    {
        var url = $"{_ollamaUrl.TrimEnd('/')}/api/chat";
        var request = new OllamaChatRequest
        {
            Model = _model,
            Messages = messages.Select(m => new OllamaChatMessage { Role = m.Role, Content = m.Content }).ToList(),
            Stream = false,
        };

        try
        {
            var response = await _http.PostAsJsonAsync(url, request, JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _sawmill.Error($"Ollama API returned {response.StatusCode}: {errorBody}");
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions);
            return result?.Message?.Content;
        }
        catch (Exception e)
        {
            _sawmill.Error($"Error communicating with Ollama API: {e}");
            return null;
        }
    }

    // Ollama API request/response DTOs

    private sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OllamaChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }
    }
}
