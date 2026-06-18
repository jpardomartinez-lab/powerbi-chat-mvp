using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Identity.Client;
using System.Text;

namespace PowerBiChatMvp;

public class SchemaCache
{
    private readonly Dictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

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
            .Where(c => c.ContainsKey("TableID") && c.ContainsKey("ExplicitName"))
            .Select(c => new
            {
                TableName = tableIdToName.GetValueOrDefault(c["TableID"]?.ToString() ?? "", ""),
                ColName = c["ExplicitName"]?.ToString() ?? "",
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
            Reglas:
            - Genera únicamente DAX.
            - La consulta debe empezar por EVALUATE.
            - Usa solo tablas, columnas y medidas listadas.
            - No inventes columnas ni medidas.
            - No expliques nada.
            - No uses markdown.
            - DAX NO tiene WHERE. Para filtrar usa CALCULATETABLE o FILTER dentro de SUMMARIZECOLUMNS.
            - Para filtrar por año actual: FILTER(tabla, YEAR(tabla[columnaFecha]) = YEAR(TODAY()))
            - Para columnas tipo Text que parecen fechas, no uses funciones de fecha.
            - No uses DATESBETWEEN sobre columnas que no sean tipo Date.
            - Ejemplo filtro año: SUMMARIZECOLUMNS(tabla[col], FILTER(tabla, YEAR(tabla[fecha]) = YEAR(TODAY())), "Medida", [Medida])
            - DAX NO tiene ORDER BY ni RETURN. Para ordenar usa TOPN(N, tabla, [Medida], DESC).
            - DAX NO tiene IN con subconsultas. Usa TREATAS o FILTER con RELATED.
            - DAX NO tiene OR como palabra clave. Usa || para OR lógico y && para AND lógico.
            - Estructura correcta para top N con filtro: EVALUATE TOPN(5, SUMMARIZECOLUMNS(..., FILTER(...)), [Medida], DESC)
            - Cuando una columna existe en varias tablas, SIEMPRE califica con el nombre de tabla. Ejemplo: PROYECTOS[TOTAL_ISSUE_COUNT] no [TOTAL_ISSUE_COUNT].
            - Para horas usa Worklogs[START_DATE] y Worklogs[LOGGED_TIME]/3600. Worklogs se relaciona con Issues via Worklogs[ISSUE_ID]=Issues[ISSUE_ID].
            - Para issues usa la tabla Issues. Issues se relaciona con Projects via Issues[PROJECT_ID]=Projects[PROJECT_ID].
            - Para cruzar Worklogs con PROYECTOS usa: SUMMARIZECOLUMNS(PROYECTOS[Clave], "Horas", [Horas]) — usa medidas en vez de joins manuales.
            - Status de proyectos está en PROYECTOS[Status].
            - SUMMARIZECOLUMNS con filtro de fecha: usa CALCULATETABLE(SUMMARIZECOLUMNS(...), FILTER(tabla, condicion)) NO pongas FILTER dentro de SUMMARIZECOLUMNS directamente.
            - Sintaxis correcta con filtro: EVALUATE CALCULATETABLE(SUMMARIZECOLUMNS(tabla[col], "med", [Medida]), YEAR(Worklogs[START_DATE]) = YEAR(TODAY()))
            - Para devolver un único valor escalar usa: EVALUATE ROW("Resultado", CALCULATE([Medida]))
            - ROW() requiere siempre pares nombre-valor: ROW("nombre1", valor1, "nombre2", valor2). Nunca ROW con un solo argumento.
            - No uses tabla Calendar ni DimDate — no existen. Para mes/año usa MONTH() y YEAR() sobre columnas de fecha reales.
            - SUMMARIZECOLUMNS solo acepta columnas reales como argumentos de agrupación, NO expresiones como MONTH(). Para agrupar por mes usa ADDCOLUMNS+SUMMARIZE: EVALUATE ADDCOLUMNS(SUMMARIZE(Worklogs, Worklogs[START_DATE]), "Mes", MONTH(Worklogs[START_DATE]), "Horas", CALCULATE([Horas]))
            - Para horas por mes del año actual usa ADDCOLUMNS+SUMMARIZE: EVALUATE ADDCOLUMNS(SUMMARIZE(FILTER(Worklogs, YEAR(Worklogs[START_DATE]) = YEAR(TODAY())), Worklogs[START_DATE]), "Mes", MONTH(Worklogs[START_DATE]), "Horas", CALCULATE([Horas]))
            - Cuando SUMMARIZECOLUMNS agrupa por columna que existe en varias tablas, SIEMPRE usa nombre completo: Issues[ISSUE_STATUS_NAME].
            - Para issues sin resolver: FILTER(Issues, Issues[RESOLUTION] = BLANK() && Issues[ISSUE_STATUS_NAME] <> "Done")
            - Columnas tipo Text NO se comparan con TRUE/FALSE. Usa texto: PROYECTOS[Status] = "Active", no PROYECTOS[Status] = TRUE.
            - Columnas tipo Boolean SÍ se comparan con TRUE/FALSE: Projects[IS_PRIVATE] = TRUE.
            - El estado activo en PROYECTOS[Status] puede ser "In Progress", "Active" u otros valores texto. Usa SEARCH o múltiples valores si no sabes el exacto.
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
