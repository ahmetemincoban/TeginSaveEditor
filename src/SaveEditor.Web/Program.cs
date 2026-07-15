using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using SaveEditor.Core;

const long MaxFileSize = 64 * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxFileSize);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxFileSize);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

TempFileCleanup.CleanupOldTempFiles(Path.GetTempPath(), TimeSpan.FromDays(1));

var detector = new FormatDetector();
var sessions = new ConcurrentDictionary<string, Session>();
var logger = app.Logger;

app.MapPost("/api/upload", async (HttpRequest request) =>
{
    CleanupSessions(sessions);
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Dosya yüklenmedi." });
    if (file.Length > MaxFileSize)
        return Results.BadRequest(new { error = "Dosya çok büyük (en fazla 64 MB)." });

    byte[] data;
    using (var ms = new MemoryStream())
    {
        await file.CopyToAsync(ms);
        data = ms.ToArray();
    }

    return Detect(data, Path.GetFileName(file.FileName), form["password"].FirstOrDefault());
});

app.MapPost("/api/paste", async (HttpRequest request) =>
{
    CleanupSessions(sessions);
    var body = await JsonNode.ParseAsync(request.Body);
    string text = body?["text"]?.GetValue<string>() ?? "";
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "Metin boş." });
    byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
    return Detect(data, body?["fileName"]?.GetValue<string>() ?? "pasted.txt",
        body?["password"]?.GetValue<string>());
});

app.MapPost("/api/download/{id}", async (string id, HttpRequest request) =>
{
    if (!sessions.TryGetValue(id, out var session))
        return Results.BadRequest(new { error = "Oturum bulunamadı veya süresi doldu. Dosyayı yeniden yükleyin." });

    var sw = Stopwatch.StartNew();
    try
    {
        var body = await JsonNode.ParseAsync(request.Body, documentOptions: new()
        {
            AllowTrailingCommas = true,
            MaxDepth = 512,
        });
        session.Document.Root = body?["root"];
        byte[] bytes = detector.Encode(session.Document);
        logger.LogInformation(
            "Download succeeded: file={FileName} format={Format} durationMs={DurationMs}",
            session.Document.FileName, session.Document.FormatId, sw.ElapsedMilliseconds);
        return Results.File(bytes, "application/octet-stream", session.Document.FileName);
    }
    catch (SaveFormatException ex)
    {
        logger.LogWarning(ex,
            "Download failed: file={FileName} format={Format} durationMs={DurationMs}",
            session.Document.FileName, session.Document.FormatId, sw.ElapsedMilliseconds);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Download failed: file={FileName} format={Format} durationMs={DurationMs}",
            session.Document.FileName, session.Document.FormatId, sw.ElapsedMilliseconds);
        return Results.BadRequest(new { error = $"Kaydetme başarısız: {ex.Message}" });
    }
});

app.Run();

IResult Detect(byte[] data, string fileName, string? password)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var doc = detector.Detect(data, fileName, new ReadContext { Password = password });
        string id = Guid.NewGuid().ToString("N");
        sessions[id] = new Session(doc, DateTime.UtcNow);
        logger.LogInformation(
            "Upload succeeded: file={FileName} format={Format} sizeBytes={SizeBytes} durationMs={DurationMs}",
            fileName, doc.FormatId, data.Length, sw.ElapsedMilliseconds);
        return Results.Json(new
        {
            id,
            fileName = doc.FileName,
            format = doc.FormatId,
            formatName = doc.FormatName,
            wrappers = doc.Wrappers,
            editable = doc.Editable,
            warnings = doc.Warnings,
            size = data.Length,
            root = doc.Root,
        });
    }
    catch (SaveFormatException ex)
    {
        logger.LogWarning(ex,
            "Upload failed: file={FileName} sizeBytes={SizeBytes} durationMs={DurationMs}",
            fileName, data.Length, sw.ElapsedMilliseconds);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Upload failed: file={FileName} sizeBytes={SizeBytes} durationMs={DurationMs}",
            fileName, data.Length, sw.ElapsedMilliseconds);
        return Results.BadRequest(new { error = $"Dosya çözümlenemedi: {ex.Message}" });
    }
}

static void CleanupSessions(ConcurrentDictionary<string, Session> sessions)
{
    var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
    foreach (var (key, session) in sessions)
    {
        if (session.Created < cutoff) sessions.TryRemove(key, out _);
    }
}

internal sealed record Session(SaveDocument Document, DateTime Created);

// Exposes the top-level statement's generated Program class so
// WebApplicationFactory<Program> can be used from the test project.
public partial class Program { }
