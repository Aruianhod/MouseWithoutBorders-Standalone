using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Updates;

namespace MouseWithoutBorders.UnitTests;

[TestClass][DoNotParallelize]
public sealed class UpdateTests
{
    private string root = null!;
    [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "MWB-Update-Test-" + Guid.NewGuid()); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup() { if (Path.GetFileName(root).StartsWith("MWB-Update-Test-", StringComparison.Ordinal)) Directory.Delete(root, true); }

    private static ReleaseInfo Release() => new() { Version = "0.102.2933.8", File = "release.zip", Bytes = 10, Sha256 = new string('A', 64) };
    private static byte[] Sign(ReleaseInfo release, RSA rsa) { byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(release); return JsonSerializer.SerializeToUtf8Bytes(new SignedRelease { Payload = Convert.ToBase64String(bytes), Signature = Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }); }

    [TestMethod]
    public void SignedReleaseRequiresPinnedKeyAndRejectsTampering()
    {
        using var rsa = RSA.Create(2048); using var stranger = RSA.Create(2048);
        string key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        byte[] signed = Sign(Release(), rsa);
        Assert.AreEqual("0.102.2933.8", UpdatePackage.Verify(signed, key).Version);
        Assert.ThrowsExactly<CryptographicException>(() => UpdatePackage.Verify(signed, Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo())));
        var envelope = JsonSerializer.Deserialize<SignedRelease>(signed)!;
        var changed = Release(); changed.File = "malware.zip";
        envelope.Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(changed));
        Assert.ThrowsExactly<CryptographicException>(() => UpdatePackage.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope), key));
    }

    [TestMethod]
    public void SignedButInvalidPackageLocationOrPlatformIsRejected()
    {
        using var rsa = RSA.Create(2048); string key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        foreach (string name in new[] { "../other.zip", "C:\\other.zip", "https://host/pkg.zip", "file.zip:stream" })
        { var release = Release(); release.File = name; Assert.ThrowsExactly<InvalidDataException>(() => UpdatePackage.Verify(Sign(release, rsa), key)); }
        var invalid = Release(); invalid.Platform = "win-arm64";
        Assert.ThrowsExactly<InvalidDataException>(() => UpdatePackage.Verify(Sign(invalid, rsa), key));
    }

    [TestMethod]
    public void SameVersionAndDowngradeAreNotUpdates()
    {
        Assert.IsTrue(UpdatePackage.IsNewer(Release(), new Version("0.102.2933.7")));
        Assert.IsFalse(UpdatePackage.IsNewer(Release(), new Version("0.102.2933.8")));
        Assert.IsFalse(UpdatePackage.IsNewer(Release(), new Version("0.102.2933.9")));
    }

    [TestMethod]
    public void ArchiveHashAndLengthMustBothMatch()
    {
        string path = Path.Combine(root, "a.zip"); File.WriteAllBytes(path, new byte[10]);
        var release = Release(); release.Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        UpdatePackage.VerifyArchive(path, release);
        File.WriteAllBytes(path, new byte[11]); Assert.ThrowsExactly<InvalidDataException>(() => UpdatePackage.VerifyArchive(path, release));
        File.WriteAllBytes(path, Enumerable.Repeat((byte)1, 10).ToArray()); Assert.ThrowsExactly<InvalidDataException>(() => UpdatePackage.VerifyArchive(path, release));
    }

    [DataTestMethod]
    [DataRow("../escape.txt")][DataRow("/absolute.txt")][DataRow("a/../../escape.txt")][DataRow("a:stream")]
    [DataRow("CON.txt")][DataRow("trailing. ")][DataRow("settings.json")][DataRow("sub/audio.json.bak")]
    public void UnsafeOrPersonalConfigurationEntriesAreRejected(string entry)
    {
        string zip = Path.Combine(root, "test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry(entry).Open()); writer.Write("data"); }
        Assert.ThrowsExactly<InvalidDataException>(() => UpdatePackage.Extract(zip, Path.Combine(root, "stage")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "escape.txt")));
    }

    [TestMethod]
    public void SwapRetainsOldVersionAndExternalSettings()
    {
        var (install, stage, backup) = PrepareSwap();
        string settings = Path.Combine(root, "user-settings.json"); File.WriteAllText(settings, "pairing-and-audio");
        bool launched = false; UpdatePackage.Swap(install, stage, backup, () => launched = true);
        Assert.IsTrue(launched); Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "version")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(backup, "version")));
        Assert.AreEqual("pairing-and-audio", File.ReadAllText(settings));
    }

    [DataTestMethod][DataRow(false)][DataRow(true)]
    public void ActivationOrLaunchFailureRestoresPreviousDirectory(bool launchFailure)
    {
        var (install, stage, backup) = PrepareSwap();
        Action fail = () => throw new IOException("injected failure");
        Assert.ThrowsExactly<IOException>(() => UpdatePackage.Swap(install, stage, backup, launchFailure ? fail : () => { }, launchFailure ? null : fail));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "version")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(stage, "version")));
        Assert.IsFalse(Directory.Exists(backup));
    }

    [TestMethod]
    public void PortableDataInsideInstallIsNeverReplaced()
    {
        Assert.ThrowsExactly<IOException>(() => UpdatePackage.ValidateInstall(root, Path.Combine(root, "data")));
    }

    [TestMethod]
    public async Task DownloadStopsOnOversizeOrCancellation()
    {
        using var input = new MemoryStream(new byte[100]); using var output = new MemoryStream();
        await Assert.ThrowsExactlyAsync<IOException>(() => UpdateClient.CopyBounded(input, output, 10, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); input.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => UpdateClient.CopyBounded(input, output, 200, cancelled.Token));
    }

    private (string, string, string) PrepareSwap()
    {
        string install = Path.Combine(root, "app"), stage = Path.Combine(root, "stage"), backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(install); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(install, "version"), "old"); File.WriteAllText(Path.Combine(stage, "version"), "new");
        return (install, stage, backup);
    }

    [TestMethod]
    public void UpdateAndFileWindowsRenderWithoutNetworkOrFileTransfer()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                foreach (System.Windows.Forms.Form form in new System.Windows.Forms.Form[] { new UpdateForm(), new MouseWithoutBorders.Files.FileTransferForm() })
                {
                    using (form)
                    {
                        form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); form.PerformLayout();
                        using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                        Directory.CreateDirectory("test-results");
                        bitmap.Save("test-results/" + form.GetType().Name + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
