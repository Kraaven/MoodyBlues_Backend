using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Scenes.Processing;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>The scene file endpoint (raw / optimized / pre-gzipped), the processing step that writes the gzip copy, and its deletion and accounting.</summary>
public class SceneServingTests : IDisposable
{
    private static readonly string Hash = new('a', 64);
    private static readonly Guid Project = Guid.NewGuid();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-serve-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly byte[] _optimized = IngestEndpointsTests.FakeGlb(4096);

    public SceneServingTests()
    {
        _paths = new StoragePaths(new ServerConfig { ScenesDir = Path.Combine(_root, "scenes"), SessionsDir = Path.Combine(_root, "sessions") });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string RevisionDir => _paths.SceneFull(StoragePaths.SceneRevisionDir(Project, "scene1", Hash));

    private async Task<MoodyBluesDbContext> SeedAsync(bool writeGzip, SceneProcessingStatus status = SceneProcessingStatus.Ready)
    {
        var db = TestDbContextFactory.Create();
        await IngestEndpointsTests.SeedRevisionAsync(db, Project, "scene1", Hash);
        SceneRevision r = (await db.SceneRevisions.FindAsync(Project, "scene1", Hash))!;
        Directory.CreateDirectory(RevisionDir);
        await File.WriteAllBytesAsync(_paths.SceneFull(r.RawGlbPath), IngestEndpointsTests.FakeGlb(100));

        r.Status = status;
        r.OptimizedGlbPath = Path.Combine(StoragePaths.SceneRevisionDir(Project, "scene1", Hash), StoragePaths.OptimizedGlbFile);
        r.OptimizedSizeBytes = _optimized.Length;
        await File.WriteAllBytesAsync(_paths.SceneFull(r.OptimizedGlbPath), _optimized);
        if (writeGzip)
        {
            await WriteGzipAsync();
        }

        await db.SaveChangesAsync();
        return db;
    }

    private async Task WriteGzipAsync()
    {
        await using var file = File.Create(Path.Combine(RevisionDir, StoragePaths.OptimizedGlbGzipFile));
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        await gzip.WriteAsync(_optimized);
    }

    private async Task<(PhysicalFileHttpResult File, HttpContext Http)> ServeAsync(MoodyBluesDbContext db, string? acceptEncoding)
    {
        var http = new DefaultHttpContext();
        if (acceptEncoding is not null)
        {
            http.Request.Headers.AcceptEncoding = acceptEncoding;
        }

        IResult result = await SceneEndpoints.ServeRevisionAsync(Project, "scene1", Hash, db, _paths, http);
        return (Assert.IsType<PhysicalFileHttpResult>(result), http);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br, gzip;q=0.8, deflate")]
    [InlineData("GZIP")]
    public async Task Optimized_WithGzipAccepted_ServesPrecompressedCopy(string acceptEncoding)
    {
        using var db = await SeedAsync(writeGzip: true);

        var (file, http) = await ServeAsync(db, acceptEncoding);

        Assert.EndsWith(StoragePaths.OptimizedGlbGzipFile, file.FileName);
        Assert.Equal("model/gltf-binary", file.ContentType);
        Assert.False(file.EnableRangeProcessing);
        Assert.Equal("gzip", http.Response.Headers.ContentEncoding.ToString());
        Assert.Equal("Accept-Encoding", http.Response.Headers.Vary.ToString());
        Assert.Equal(_optimized.Length.ToString(), http.Response.Headers[SceneEndpoints.DecodedLengthHeader].ToString());
        Assert.StartsWith("private, max-age=31536000", http.Response.Headers.CacheControl.ToString());

        await using var gz = new GZipStream(File.OpenRead(file.FileName), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await gz.CopyToAsync(decoded);
        Assert.Equal(_optimized, decoded.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("br, deflate")]
    [InlineData("gzip;q=0")]
    public async Task Optimized_WithoutGzipAccepted_ServesPlainFile(string? acceptEncoding)
    {
        using var db = await SeedAsync(writeGzip: true);

        var (file, http) = await ServeAsync(db, acceptEncoding);

        Assert.EndsWith(StoragePaths.OptimizedGlbFile, file.FileName);
        Assert.True(file.EnableRangeProcessing);
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.ContentEncoding.ToString()));
        Assert.Equal("Accept-Encoding", http.Response.Headers.Vary.ToString());
        Assert.Equal(_optimized.Length.ToString(), http.Response.Headers[SceneEndpoints.DecodedLengthHeader].ToString());
    }

    [Fact]
    public async Task Optimized_WithoutGzipFile_ServesPlainFile_EvenWhenGzipAccepted()
    {
        using var db = await SeedAsync(writeGzip: false);

        var (file, http) = await ServeAsync(db, "gzip");

        Assert.EndsWith(StoragePaths.OptimizedGlbFile, file.FileName);
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.ContentEncoding.ToString()));
        Assert.Equal(_optimized.Length.ToString(), http.Response.Headers[SceneEndpoints.DecodedLengthHeader].ToString());
    }

    [Fact]
    public async Task Raw_IsNeverServedGzipped_AndStillReportsDecodedLength()
    {
        using var db = await SeedAsync(writeGzip: true, status: SceneProcessingStatus.Failed);

        var (file, http) = await ServeAsync(db, "gzip");

        Assert.EndsWith("raw.glb", file.FileName);
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.ContentEncoding.ToString()));
        Assert.Equal("100", http.Response.Headers[SceneEndpoints.DecodedLengthHeader].ToString());
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.CacheControl.ToString()));
    }

    private sealed class FakeOptimizer(byte[] output) : IGltfOptimizer
    {
        public async Task<bool> OptimizeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(outputPath, output, cancellationToken);
            return true;
        }
    }

    [Fact]
    public async Task Processing_WritesGzipCopy_AndRecordsItsSize()
    {
        using var db = TestDbContextFactory.Create();
        await IngestEndpointsTests.SeedRevisionAsync(db, Project, "scene1", Hash);
        SceneRevision r = (await db.SceneRevisions.FindAsync(Project, "scene1", Hash))!;
        r.RegistryReady = true;
        Directory.CreateDirectory(RevisionDir);
        await File.WriteAllBytesAsync(_paths.SceneFull(r.RawGlbPath), IngestEndpointsTests.FakeGlb(100));
        await db.SaveChangesAsync();

        var worker = new SceneProcessingWorker(null!, _paths, new SceneWorkSignal(), new CompileWorkSignal());
        await worker.ProcessAsync(r, db, new FakeOptimizer(_optimized), CancellationToken.None);

        Assert.Equal(SceneProcessingStatus.Ready, r.Status);
        string gzipPath = Path.Combine(RevisionDir, StoragePaths.OptimizedGlbGzipFile);
        Assert.Equal(new FileInfo(gzipPath).Length, r.OptimizedGzipSizeBytes);
        await using var gz = new GZipStream(File.OpenRead(gzipPath), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await gz.CopyToAsync(decoded);
        Assert.Equal(_optimized, decoded.ToArray());
        Assert.Equal(r.RawSizeBytes + r.OptimizedSizeBytes + r.OptimizedGzipSizeBytes, await StorageUsedAsync(db));
    }

    private static async Task<long> StorageUsedAsync(MoodyBluesDbContext db) => await StorageQuota.UsedBytesAsync(db, Project);

    [Fact]
    public async Task DeletingTheVersion_RemovesTheGzipCopy_AndCountsItsBytes()
    {
        using var db = await SeedAsync(writeGzip: true);
        SceneRevision r = (await db.SceneRevisions.FindAsync(Project, "scene1", Hash))!;
        r.OptimizedGzipSizeBytes = 777;
        await db.SaveChangesAsync();
        string gzipPath = Path.Combine(RevisionDir, StoragePaths.OptimizedGlbGzipFile);
        Assert.True(File.Exists(gzipPath));
        Assert.Equal(r.RawSizeBytes + _optimized.Length + 777, await StorageUsedAsync(db));

        DeletionResult result = await SceneDeletion.DeleteAsync(db, _paths, new ActiveConnections(), Project, "scene1", [r], deleteScene: true);

        Assert.Equal(r.RawSizeBytes + _optimized.Length + 777, result.Bytes);
        Assert.False(File.Exists(gzipPath));
        Assert.False(Directory.Exists(RevisionDir));
    }

    [Fact]
    public void OptimizerArguments_AreScriptThenInputThenOutput()
    {
        Assert.Equal(["/opt/s.mjs", "in.glb", "out.glb"], NodeGltfOptimizer.BuildArguments("/opt/s.mjs", "in.glb", "out.glb"));
    }
}
