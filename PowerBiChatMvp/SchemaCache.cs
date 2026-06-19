using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Identity.Client;
using PowerBiChatMvp.Controllers;
using System.Text;

namespace PowerBiChatMvp;

public class SchemaCache
{
    private readonly Dictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void ClearCache(string? workspaceName = null, string? datasetName = null)
    {
        if (workspaceName != null && datasetName != null)
            _cache.Remove($"{workspaceName}|{datasetName}");
        else if (workspaceName != null) // clave literal (ej: "ssas|server|db")
            _cache.Remove(workspaceName);
        else
            _cache.Clear();
    }

    public async Task<string> GetPromptSsasAsync(string server, string database)
    {
        var key = $"ssas|{server}|{database}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;

            using var conn = DaxController.OpenSsasConnection(server, database);
            var tables  = QuerySchema(conn, "SELECT * FROM $SYSTEM.TMSCHEMA_TABLES");
            var columns = QuerySchema(conn, "SELECT * FROM $SYSTEM.TMSCHEMA_COLUMNS");
            var measures= QuerySchema(conn, "SELECT * FROM $SYSTEM.TMSCHEMA_MEASURES");

            var prompt = BuildPrompt(database, tables, columns, measures);
            _cache[key] = prompt;
            return prompt;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetPromptAsync(IConfiguration config, string workspaceName, string datasetName)
    {
        var key = $"{workspaceName}|{datasetName}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;
            var prompt = await BuildPromptAsync(config, workspaceName, datasetName);
            _cache[key] = prompt;
            return prompt;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<string> BuildPromptAsync(IConfiguration config, string workspaceName, string datasetName)
    {
        var tenantId = config["PowerBI:TenantId"];
        var clientId = config["PowerBI:ClientId"];
        var clientSecret = config["PowerBI:ClientSecret"];

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

        using var connection = new AdomdConnection(connectionString);
        connection.AccessToken = new Microsoft.AnalysisServices.AccessToken(
            auth.AccessToken, auth.ExpiresOn.UtcDateTime);
        connection.Open();

        var tables = QuerySchema(connection, "SELECT * FROM $SYSTEM.TMSCHEMA_TABLES");
        var columns = QuerySchema(connection, "SELECT * FROM $SYSTEM.TMSCHEMA_COLUMNS");
        var measures = QuerySchema(connection, "SELECT * FROM $SYSTEM.TMSCHEMA_MEASURES");

        return BuildPrompt(datasetName, tables, columns, measures);
    }

    private static List<Dictionary<string, object?>> QuerySchema(AdomdConnection connection, string dax)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = dax;
        using var reader = cmd.ExecuteReader();

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

    private static string BuildPrompt(
        string datasetName,
        List<Dictionary<string, object?>> tables,
        List<Dictionary<string, object?>> columns,
        List<Dictionary<string, object?>> measures)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Modelo Power BI: {datasetName}");
        sb.AppendLine();

        // Build table ID -> name lookup from TMSCHEMA_TABLES
        // Keys: ID, Name, IsHidden, ...
        var tableIdToName = tables
            .Where(t => t.ContainsKey("ID") && t.ContainsKey("Name"))
            .ToDictionary(
                t => t["ID"]?.ToString() ?? "",
                t => t["Name"]?.ToString() ?? ""
            );

        // Filter system tables
        var systemTablePrefixes = new[] { "DateTableTemplate", "LocalDateTable" };

        // Columns to exclude (internal/technical, unlikely to be queried)
        var excludedColPrefixes = new[] { "RowNumber-", "_CHART_" };
        var excludedColExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SystemFlags", "DisplayOrdinal", "ColumnStorageID", "AttributeHierarchyID",
            "StructureModifiedTime", "RefreshedTime", "ModifiedTime"
        };
        // Regex para columnas Jira con sufijo numérico: Name_12345 o Name_12345_extra
        var jiraCustomFieldPattern = new System.Text.RegularExpressions.Regex(@"_\d{4,}(_\d+)?$");

        // Group columns by table name via TableID join
        var colsByTable = columns
            .Where(c => c.ContainsKey("TableID") && (c.ContainsKey("ExplicitName") || c.ContainsKey("Name")))
            .Select(c => new
            {
                TableName = tableIdToName.GetValueOrDefault(c["TableID"]?.ToString() ?? "", ""),
                ColName = (c.GetValueOrDefault("ExplicitName")?.ToString()?.Trim() is { Length: > 0 } en ? en
                          : c.GetValueOrDefault("Name")?.ToString() ?? ""),
                DataType = c.ContainsKey("ExplicitDataType") ? c["ExplicitDataType"]?.ToString() ?? "" : ""
            })
            .Where(c => !string.IsNullOrEmpty(c.TableName)
                     && !string.IsNullOrEmpty(c.ColName)
                     && !systemTablePrefixes.Any(p => c.TableName.StartsWith(p))
                     && !excludedColPrefixes.Any(p => c.ColName.StartsWith(p))
                     && !excludedColExact.Contains(c.ColName)
                     && !jiraCustomFieldPattern.IsMatch(c.ColName))
            .GroupBy(c => c.TableName)
            .OrderBy(g => g.Key);

        foreach (var table in colsByTable)
        {
            sb.AppendLine($"Tabla {table.Key}:");
            foreach (var col in table)
            {
                var dataType = MapDataType(col.DataType);
                sb.AppendLine($"  - {col.ColName} ({dataType})");
            }
            sb.AppendLine();
        }

        // Measures grouped by table name via TableID join
        var measByTable = measures
            .Where(m => m.ContainsKey("TableID") && m.ContainsKey("Name"))
            .Select(m => new
            {
                TableName = tableIdToName.GetValueOrDefault(m["TableID"]?.ToString() ?? "", ""),
                MeasName = m["Name"]?.ToString() ?? ""
            })
            .Where(m => !string.IsNullOrEmpty(m.TableName) && !string.IsNullOrEmpty(m.MeasName))
            .GroupBy(m => m.TableName)
            .OrderBy(g => g.Key);

        sb.AppendLine("Medidas disponibles:");
        foreach (var table in measByTable)
            foreach (var m in table)
                sb.AppendLine($"  - [{m.MeasName}]");

        sb.AppendLine();
        sb.AppendLine("""
            Reglas DAX:
            - Genera únicamente DAX. La consulta debe empezar por EVALUATE.
            - Usa solo tablas, columnas y medidas listadas arriba. No inventes nombres.
            - No expliques nada. No uses markdown.
            - DAX NUNCA usa WHERE. WHERE no existe en DAX. Está PROHIBIDO escribir WHERE. Para filtrar: CALCULATETABLE o FILTER.
            - Ejemplo CORRECTO con filtro año: EVALUATE CALCULATETABLE(SUMMARIZECOLUMNS(Tabla[Empresa], "Fact", [Medida]), YEAR(Tabla[Fecha]) = 2025)
            - Ejemplo INCORRECTO (NUNCA hacer): EVALUATE SUMMARIZECOLUMNS(...) WHERE YEAR(...) = 2025
            - DAX NO tiene ORDER BY ni RETURN. Para ordenar usa TOPN(N, tabla, [Medida], DESC).
            - DAX NO tiene OR como palabra clave. Usa || para OR lógico, && para AND lógico.
            - DAX NO tiene IN con subconsultas. Usa TREATAS o FILTER con RELATED.
            - SUMMARIZECOLUMNS solo acepta columnas reales como argumentos de agrupación, NO expresiones.
            - SUMMARIZECOLUMNS con filtro: usa CALCULATETABLE(SUMMARIZECOLUMNS(...), condicion) — NO pongas FILTER dentro de SUMMARIZECOLUMNS directamente.
            - Para agrupar por expresión (mes, año...) usa ADDCOLUMNS sobre SUMMARIZE.
            - Para filtrar por año actual: CALCULATETABLE(SUMMARIZECOLUMNS(...), YEAR(Tabla[Fecha]) = YEAR(TODAY()))
            - Para Top N: EVALUATE TOPN(5, SUMMARIZECOLUMNS(Tabla[Col], "Medida", [Medida]), [Medida], DESC)
            - Para devolver escalar: EVALUATE ROW("Resultado", CALCULATE([Medida]))
            - ROW() requiere pares nombre-valor: ROW("n1", v1, "n2", v2). Nunca ROW con un solo argumento.
            - Cuando una columna existe en varias tablas, SIEMPRE califica con el nombre de tabla: Tabla[Columna].
            - Columnas tipo Text NO se comparan con TRUE/FALSE. Columnas tipo Boolean SÍ.
            - Para columnas tipo Text que parecen fechas, no uses funciones de fecha DAX.
            """);

        return sb.ToString();
    }

    private static string MapDataType(string dataType) => dataType switch
    {
        "2" => "Text",
        "6" => "Whole Number",
        "8" => "Decimal",
        "9" => "Date",
        "10" => "DateTime",
        "11" => "Boolean",
        _ => $"Type{dataType}"
    };
}
