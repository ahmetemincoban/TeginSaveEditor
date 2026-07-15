using System.Collections.Concurrent;
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

var detector = new FormatDetector();
var sessions = new ConcurrentDictionary<string, Session>();

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

    try
    {
        var body = await JsonNode.ParseAsync(request.Body, documentOptions: new()
        {
            AllowTrailingCommas = true,
            MaxDepth = 512,
        });
        session.Document.Root = body?["root"];
        byte[] bytes = detector.Encode(session.Document);
        return Results.File(bytes, "application/octet-stream", session.Document.FileName);
    }
    catch (SaveFormatException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Kaydetme başarısız: {ex.Message}" });
    }
});

app.Run();

IResult Detect(byte[] data, string fileName, string? password)
{
    try
    {
        var doc = detector.Detect(data, fileName, new ReadContext { Password = password });
        string id = Guid.NewGuid().ToString("N");
        sessions[id] = new Session(doc, DateTime.UtcNow);
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
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
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
