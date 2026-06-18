using Microsoft.AspNetCore.Mvc;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Identity.Client;
using Anthropic;
using Anthropic.Models.Messages;

namespace PowerBiChatMvp.Controllers;

public class ChatRequest
{
    public string Question { get; set; } = string.Empty;
}

[ApiController]
[Route("api/[controller]")]
public class DaxController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly SchemaCache _schema;

    public DaxController(IConfiguration config, SchemaCache schema)
    {
        _config = config;
        _schema = schema;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request)
    {
        await _schema.EnsureLoadedAsync(_config);

        var dax = await GenerateDaxAsync(request.Question);

        // Limpiar bloques markdown si Claude los añadió
        dax = System.Text.RegularExpressions.Regex.Replace(dax, @"```[a-zA-Z]*\s*", "").Replace("```", "").Trim();

        // Si Claude omitió EVALUATE, añadirlo
        if (!dax.TrimStart().StartsWith("EVALUATE", StringComparison.OrdinalIgnoreCase))
            dax = "EVALUATE\n" + dax.TrimStart();

        ValidateDax(dax);

        var rows = await ExecuteDaxAsync(dax);

        var rowsForAnswer = rows.Count > 100 ? rows.Take(100).ToList() : rows;
        var answer = await GenerateAnswerAsync(request.Question, dax, rowsForAnswer, rows.Count);

        return Ok(new { question = request.Question, answer, dax, rows });
    }

    [HttpGet("test")]
    public async Task<IActionResult> Test()
    {
        await _schema.EnsureLoadedAsync(_config);
        return Ok(new { schema = _schema.Prompt });
    }

    [HttpGet("schema-keys")]
    public async Task<IActionResult> SchemaKeys()
    {
        var (connection, _) = await OpenConnectionAsync();
        using var conn = connection;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM $SYSTEM.MDSCHEMA_COLUMNS";
        using var reader = cmd.ExecuteReader();

        var keys = new List<string>();
        if (reader.Read())
            for (var i = 0; i < reader.FieldCount; i++)
                keys.Add(reader.GetName(i));

        return Ok(keys);
    }

    private async Task<string> GenerateDaxAsync(string question)
    {
        var client = new AnthropicClient() { ApiKey = _config["Anthropic:ApiKey"] };

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = "claude-haiku-4-5",
            MaxTokens = 1024,
            System = _schema.Prompt,
            Messages = [new() { Role = Role.User, Content = question }]
        });

        return response.Content.Select(b => b.Value).OfType<TextBlock>()
                       .FirstOrDefault()?.Text.Trim() ?? string.Empty;
    }

    private async Task<string> GenerateAnswerAsync(string question, string dax, List<Dictionary<string, object?>> rows, int totalRows = -1)
    {
        var client = new AnthropicClient() { ApiKey = _config["Anthropic:ApiKey"] };

        var rowsJson = System.Text.Json.JsonSerializer.Serialize(rows);
        var totalNote = totalRows > rows.Count ? $"\n(Mostrando {rows.Count} de {totalRows} filas totales)" : "";

        var userPrompt = $"""
            Pregunta del usuario:
            {question}

            DAX ejecutado:
            {dax}

            Resultado JSON:{totalNote}
            {rowsJson}

            Redacta una respuesta final para el usuario.
            """;

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = "claude-haiku-4-5",
            MaxTokens = 1024,
            System = """
                Eres un asistente de análisis de datos.
                Responde en español de forma clara y breve.
                Usa únicamente los datos recibidos.
                No inventes información.
                Si hay filas, resume los resultados principales.
                Si no hay filas, indica que no hay datos para responder.
                """,
            Messages = [new() { Role = Role.User, Content = userPrompt }]
        });

        return response.Content.Select(b => b.Value).OfType<TextBlock>()
                       .FirstOrDefault()?.Text.Trim() ?? string.Empty;
    }

    private static void ValidateDax(string dax)
    {
        var normalized = dax.Trim().ToUpperInvariant();

        if (!normalized.StartsWith("EVALUATE"))
            throw new InvalidOperationException("La consulta DAX debe empezar por EVALUATE.");

        var blocked = new[] { @"\bALTER\b", @"\bINSERT\b", @"\bDROP\b", @"\$SYSTEM\.TMSCHEMA" };

        foreach (var pattern in blocked)
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, pattern))
                throw new InvalidOperationException($"DAX bloqueado por contener patrón: {pattern}");
    }

    private async Task<List<Dictionary<string, object?>>> ExecuteDaxAsync(string dax)
    {
        var (connection, _) = await OpenConnectionAsync();
        using var conn = connection;

        using var command = conn.CreateCommand();
        command.CommandText = dax;
        using var reader = command.ExecuteReader();

        var result = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }

        return result;
    }

    internal async Task<(AdomdConnection, string)> OpenConnectionAsync()
    {
        var tenantId = _config["PowerBI:TenantId"];
        var clientId = _config["PowerBI:ClientId"];
        var clientSecret = _config["PowerBI:ClientSecret"];
        var workspaceName = _config["PowerBI:WorkspaceName"];
        var datasetName = _config["PowerBI:DatasetName"];

        var app = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();

        var auth = await app
            .AcquireTokenForClient(["https://analysis.windows.net/powerbi/api/.default"])
            .ExecuteAsync();

        var connectionString =
            $"Data Source=powerbi://api.powerbi.com/v1.0/myorg/{workspaceName};" +
            $"Initial Catalog={datasetName};";

        var connection = new AdomdConnection(connectionString);
        connection.AccessToken = new Microsoft.AnalysisServices.AccessToken(
            auth.AccessToken, auth.ExpiresOn.UtcDateTime);
        connection.Open();

        return (connection, datasetName!);
    }
}
