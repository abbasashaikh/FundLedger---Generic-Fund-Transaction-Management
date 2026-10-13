using System.Net;
using System.Text;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;
using FundLedger.Infrastructure.Storage;
using SkiaSharp;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Files attached to entries (PRD §18, TRD §9: TR-050..054).</summary>
[Collection(PostgresTests.Name)]
public sealed class AttachmentTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ---- test files ------------------------------------------------------------------------------------------
    private static byte[] Jpeg(int w, int h, byte[]? beforeScan = null)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(0, 0, w / 4f, h / 4f, paint);                        // a mark in the top-left corner, to see rotation
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var bytes = data.ToArray();
        return beforeScan is null ? bytes : [.. bytes[..2], .. beforeScan, .. bytes[2..]];     // an extra segment right after the start-of-image marker
    }

    /// <summary>An EXIF block (APP1) with a GPS-looking tag text, and optionally an orientation.</summary>
    private static byte[] Exif(int? orientation = null)
    {
        var tiff = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 };
        tiff.AddRange([0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)(orientation ?? 1), 0, 0, 0, 0, 0, 0]);   // one tag: Orientation
        var body = new List<byte>(Encoding.ASCII.GetBytes("Exif\0\0"));
        body.AddRange(tiff);
        body.AddRange(Encoding.ASCII.GetBytes("GPSLatitude=19.0760N;GPSLongitude=72.8777E;Make=SecretPhone"));
        var length = body.Count + 2;
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF), .. body];
    }

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");

    private static bool Contains(byte[] haystack, string needle) => Encoding.Latin1.GetString(haystack).Contains(needle, StringComparison.Ordinal);

    private async Task<(Books B, Guid Txn)> WithEntryAsync()
    {
        var b = await Books.CreateAsync(_host);
        var (id, _, _) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit());
        return (b, id);
    }

    // ---- uploading ----------------------------------------------------------------------------------------
    [Fact]
    public async Task A_photo_is_re_encoded_without_its_location_and_turned_upright_and_can_be_downloaded()
    {
        SkipIfNoDb();
        var (b, txn) = await WithEntryAsync();
        var photo = Jpeg(200, 100, Exif(orientation: 6));               // taken sideways: "rotate 90° clockwise"
        Assert.True(Contains(photo, "GPSLatitude"));

        var response = await b.Admin.UploadAsync($"/api/v1/transactions/{txn}/attachments", "..\\..\\IMG_0042.JPEG", photo, "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Books.JsonAsync(response);
        Assert.Equal("IMG_0042.jpg", Str(created, "fileName"));          // no path, extension matches the real type
        Assert.Equal("image/jpeg", Str(created, "contentType"));

        var download = await b.Admin.GetAsync($"/api/v1/attachments/{created.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var stored = await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.False(Contains(stored, "GPSLatitude"));                   // TR-051: the location is gone
        Assert.False(Contains(stored, "SecretPhone"));
        Assert.False(Contains(stored, "Exif"));
        using var decoded = SKBitmap.Decode(stored);
        Assert.Equal((100, 200), (decoded.Width, decoded.Height));       // orientation applied: now portrait
        Assert.Equal(created.GetProperty("sizeBytes").GetInt32(), stored.Length);

        var audit = (await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=ATTACHMENT_ADDED")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(txn.ToString(), Str(audit, "entityId"));
    }

    [Fact]
    public async Task Large_photos_are_scaled_down_and_pdfs_are_kept_exactly_as_uploaded()
    {
        SkipIfNoDb();
        var (b, txn) = await WithEntryAsync();
        var big = await Books.JsonAsync(await b.Admin.UploadAsync($"/api/v1/transactions/{txn}/attachments", "scan.jpg", Jpeg(3000, 2000), "image/jpeg"));
        var bigBytes = await (await b.Admin.GetAsync($"/api/v1/attachments/{big.GetProperty("id").GetGuid()}")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using (var decoded = SKBitmap.Decode(bigBytes))
        {
            Assert.Equal((2000, 1333), (decoded.Width, decoded.Height));
        }

        var pdf = Pdf();
        var created = await Books.JsonAsync(await b.Admin.UploadAsync($"/api/v1/transactions/{txn}/attachments", "bill.pdf", pdf, "application/pdf"));
        Assert.Equal("application/pdf", Str(created, "contentType"));
        var back = await (await b.Admin.GetAsync($"/api/v1/attachments/{created.GetProperty("id").GetGuid()}")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(pdf, back);

        var list = await b.Admin.GetJsonAsync($"/api/v1/transactions/{txn}/attachments");
        Assert.Equal(2, list.GetArrayLength());
    }

    [Fact]
    public async Task The_type_is_judged_by_the_content_not_the_name_or_the_declared_type()
    {
        SkipIfNoDb();
        var (b, txn) = await WithEntryAsync();
        var url = $"/api/v1/transactions/{txn}/attachments";

        // A PDF named .jpg is a PDF; a text file or script named .jpg is refused; so is a broken photo.
        var pdf = await Books.JsonAsync(await b.Admin.UploadAsync(url, "photo.jpg", Pdf(), "image/jpeg"));
        Assert.Equal(("application/pdf", "photo.pdf"), (Str(pdf, "contentType"), Str(pdf, "fileName")));
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync(url, "photo.jpg", Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "image/jpeg"), (HttpStatusCode)415, "ATTACHMENT_TYPE_NOT_ALLOWED");
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync(url, "run.exe", [0x4D, 0x5A, 0x90, 0x00, 0x03], "application/pdf"), (HttpStatusCode)415, "ATTACHMENT_TYPE_NOT_ALLOWED");
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync(url, "broken.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8], "image/jpeg"), (HttpStatusCode)415, "ATTACHMENT_TYPE_NOT_ALLOWED");
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync(url, "empty.jpg", [], "image/jpeg"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(1, (await b.Admin.GetJsonAsync(url)).GetArrayLength());
    }

    [Fact]
    public async Task The_size_limit_is_the_organizations_setting_and_five_files_is_the_most_per_entry()
    {
        SkipIfNoDb();
        var (b, txn) = await WithEntryAsync();
        var url = $"/api/v1/transactions/{txn}/attachments";

        var settings = await b.Admin.GetJsonAsync("/api/v1/settings");
        Assert.Equal(5, settings.GetProperty("attachments").GetProperty("maxMb").GetInt32());
        var tiny = System.Text.Json.Nodes.JsonNode.Parse(settings.GetRawText())!.AsObject();
        tiny["attachments"]!["maxMb"] = 1;
        (await b.Admin.PutAsync("/api/v1/settings", JsonSerializer.Deserialize<Dictionary<string, object?>>(tiny.ToJsonString()))).EnsureSuccessStatusCode();

        var bigPdf = Pdf().Concat(new byte[1_100_000]).ToArray();
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync(url, "huge.pdf", bigPdf, "application/pdf"), (HttpStatusCode)413, "ATTACHMENT_TOO_LARGE");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await b.Admin.UploadAsync(url, $"bill{i}.pdf", Pdf(), "application/pdf")).StatusCode);
        }

        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync(url, "sixth.pdf", Pdf(), "application/pdf"), HttpStatusCode.Conflict, "ATTACHMENT_LIMIT");

        // Hiding one frees a place (hidden files are kept, but no longer count).
        var first = (await b.Admin.GetJsonAsync(url))[0].GetProperty("id").GetGuid();
        (await b.Admin.PostAsync($"/api/v1/attachments/{first}/hide", new { reason = "Wrong bill uploaded" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.UploadAsync(url, "sixth.pdf", Pdf(), "application/pdf")).StatusCode);
    }

    // ---- who may attach, see and hide -----------------------------------------------------------------------
    [Fact]
    public async Task Members_attach_to_their_own_entries_only_and_never_to_cancelled_entries_or_closed_funds()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var mine = await b.MemberAsync(name: "Owner");
        var other = await b.MemberAsync(name: "Other");
        var (ownEntry, _, _) = await Books.RecordAsync(mine.Session, "deposit", b.Deposit());
        var (adminEntry, adminRev, _) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("5.00"));

        Assert.Equal(HttpStatusCode.Created, (await mine.Session.UploadAsync($"/api/v1/transactions/{ownEntry}/attachments", "a.pdf", Pdf(), "application/pdf")).StatusCode);
        await ProblemAssert.HasCodeAsync(await other.Session.UploadAsync($"/api/v1/transactions/{ownEntry}/attachments", "a.pdf", Pdf(), "application/pdf"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await mine.Session.UploadAsync($"/api/v1/transactions/{adminEntry}/attachments", "a.pdf", Pdf(), "application/pdf"), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.UploadAsync($"/api/v1/transactions/{ownEntry}/attachments", "admin.pdf", Pdf(), "application/pdf")).StatusCode);   // Admin: any entry

        (await b.Admin.PostAsync($"/api/v1/transactions/{adminEntry}/cancel", new { reason = "Entered twice" }, adminRev)).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync($"/api/v1/transactions/{adminEntry}/attachments", "a.pdf", Pdf(), "application/pdf"), HttpStatusCode.Conflict, "TXN_CANCELLED_IMMUTABLE");

        (await b.Admin.PostAsync($"/api/v1/funds/{b.FundId}/close", new { reason = "done" })).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await mine.Session.UploadAsync($"/api/v1/transactions/{ownEntry}/attachments", "b.pdf", Pdf(), "application/pdf"), HttpStatusCode.Conflict, "FUND_NOT_ACTIVE");
    }

    [Fact]
    public async Task Files_are_seen_only_by_people_who_can_see_the_entry_and_other_organizations_see_nothing()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var owner = await b.MemberAsync(viewAll: false, name: "Owner");
        var neighbour = await b.MemberAsync(viewAll: false, name: "Neighbour");     // may not see other people's entries
        var (entry, _, _) = await Books.RecordAsync(owner.Session, "deposit", b.Deposit());
        var file = await Books.JsonAsync(await owner.Session.UploadAsync($"/api/v1/transactions/{entry}/attachments", "a.pdf", Pdf(), "application/pdf"));
        var id = file.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await owner.Session.GetAsync($"/api/v1/attachments/{id}")).StatusCode);
        await ProblemAssert.HasCodeAsync(await neighbour.Session.GetAsync($"/api/v1/attachments/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await neighbour.Session.GetAsync($"/api/v1/transactions/{entry}/attachments"), HttpStatusCode.NotFound, "NOT_FOUND");

        var intruder = await (await _host.CreateOrgAsync()).AdminAsync();
        await ProblemAssert.HasCodeAsync(await intruder.GetAsync($"/api/v1/attachments/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await intruder.GetAsync($"/api/v1/transactions/{entry}/attachments"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await intruder.UploadAsync($"/api/v1/transactions/{entry}/attachments", "a.pdf", Pdf(), "application/pdf"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await intruder.PostAsync($"/api/v1/attachments/{id}/hide", new { reason = "not yours to hide" }), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task An_admin_can_hide_a_file_with_a_reason_it_stays_for_admins_and_is_gone_for_members_TR054()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync(name: "Owner");
        var (entry, _, _) = await Books.RecordAsync(member.Session, "deposit", b.Deposit());
        var id = (await Books.JsonAsync(await member.Session.UploadAsync($"/api/v1/transactions/{entry}/attachments", "a.pdf", Pdf(), "application/pdf"))).GetProperty("id").GetGuid();

        await ProblemAssert.HasCodeAsync(await member.Session.PostAsync($"/api/v1/attachments/{id}/hide", new { reason = "I changed my mind" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/attachments/{id}/hide", new { reason = "no" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var hidden = await Books.JsonAsync(await b.Admin.PostAsync($"/api/v1/attachments/{id}/hide", new { reason = "Shows another person's bank details" }));
        Assert.True(hidden.GetProperty("isHidden").GetBoolean());
        Assert.Equal("Shows another person's bank details", Str(hidden, "hiddenReason"));

        await ProblemAssert.HasCodeAsync(await member.Session.GetAsync($"/api/v1/attachments/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(0, (await member.Session.GetJsonAsync($"/api/v1/transactions/{entry}/attachments")).GetArrayLength());
        var adminList = await b.Admin.GetJsonAsync($"/api/v1/transactions/{entry}/attachments");
        Assert.True(adminList[0].GetProperty("isHidden").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await b.Admin.GetAsync($"/api/v1/attachments/{id}")).StatusCode);              // kept, never deleted

        var log = (await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=ATTACHMENT_HIDDEN")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Shows another person's bank details", Str(log, "reason"));
    }

    [Fact]
    public async Task Without_storage_set_up_uploads_say_so_and_nothing_breaks_for_the_rest()
    {
        SkipIfNoDb();
        await using var bare = new ApiHost(db, storage: "None");
        var b = await Books.CreateAsync(bare);
        var (entry, _, _) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit());
        await ProblemAssert.HasCodeAsync(await b.Admin.UploadAsync($"/api/v1/transactions/{entry}/attachments", "a.pdf", Pdf(), "application/pdf"), HttpStatusCode.ServiceUnavailable, "STORAGE_NOT_CONFIGURED");
        Assert.False((await b.Admin.GetJsonAsync("/api/v1/me")).GetProperty("organization").GetProperty("attachmentsEnabled").GetBoolean());
        Assert.Equal(0, (await b.Admin.GetJsonAsync($"/api/v1/transactions/{entry}/attachments")).GetArrayLength());
    }

    [Fact]
    public async Task The_organization_info_tells_the_app_what_is_possible()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var org = (await b.Admin.GetJsonAsync("/api/v1/me")).GetProperty("organization");
        Assert.True(org.GetProperty("attachmentsEnabled").GetBoolean());
        Assert.Equal(5, org.GetProperty("attachmentMaxMb").GetInt32());
    }

    // ---- storage ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_bucket_link_is_private_signed_and_valid_for_sixty_seconds_TR053()
    {
        using var storage = new S3ObjectStorage(new S3Options
        {
            ServiceUrl = "https://0123456789abcdef.r2.cloudflarestorage.com", Bucket = "fundledger-prod", AccessKeyId = "AKIDEXAMPLE", SecretAccessKey = "not-a-real-secret",
        });
        var url = storage.CreateDownloadUrl("org/o/fund/f/txn/t/a.pdf", TimeSpan.FromSeconds(60), "bill \"1\".pdf", "application/pdf")!;

        Assert.Equal("https", url.Scheme);
        Assert.Contains("fundledger-prod", url.ToString(), StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=60", url.Query, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=", url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-secret", url.ToString(), StringComparison.Ordinal);
        Assert.Contains("response-content-type=application%2Fpdf", url.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\fakepath\IMG_1.PNG", "image/png", "IMG_1.png")]
    [InlineData("../../etc/passwd", "image/jpeg", "passwd.jpg")]
    [InlineData("..\..\IMG_0042.JPEG", "image/jpeg", "IMG_0042.jpg")]
    [InlineData("", "application/pdf", "attachment.pdf")]
    [InlineData("bill .pdf", "application/pdf", "bill.pdf")]
    public void File_names_are_cleaned_the_same_way_on_every_platform(string name, string type, string expected) =>
        Assert.Equal(expected, FundLedger.Application.Attachments.AttachmentService.CleanName(name, type));

    [Fact]
    public async Task Local_storage_refuses_keys_that_climb_out_of_its_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "fundledger-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalObjectStorage(root);
            await storage.PutAsync("org/a/file.pdf", [1, 2, 3], "application/pdf", TestContext.Current.CancellationToken);
            await using (var read = await storage.OpenReadAsync("org/a/file.pdf", TestContext.Current.CancellationToken))
            {
                Assert.NotNull(read);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.PutAsync("../escape.pdf", [1], "application/pdf", TestContext.Current.CancellationToken));
            Assert.Null(await storage.OpenReadAsync("org/a/missing.pdf", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
