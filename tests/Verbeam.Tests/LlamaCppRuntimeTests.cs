using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class LlamaCppRuntimeTests
{
    [Fact]
    public void LlamaCppOptions_DefaultBinariesDirectory_UsesStableLocalAppDataPath()
    {
        var options = new LlamaCppOptions();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine("runtimes", "llama-cpp")
            : Path.Combine(localAppData, "Verbeam", "runtimes", "llama-cpp");

        Assert.Equal(expected, options.BinariesDirectory);
    }

    [Fact]
    public void BuiltInCatalog_DeclaresSafeLlamaCppRealtimeProfile()
    {
        var catalog = LoadModelCatalog();
        var model = Assert.Single(catalog.Models, item => item.Id == "verbeam-mort-qwen2.5-0.5b");
        var runtime = Assert.IsType<ModelLlamaCppRuntime>(model.Runtimes.LlamaCpp);
        var profile = Assert.Single(runtime.Profiles, item => item.Name == "realtime-ocr");

        Assert.Equal(2, catalog.SchemaVersion);
        Assert.Equal("Q8_0", model.Artifact?.Quant);
        Assert.Equal("Apache-2.0", model.Artifact?.License);
        Assert.Equal("b9590", runtime.MinLlamaCppVersion);
        Assert.Equal("vulkan", runtime.BinaryFlavor);
        Assert.Contains("llama-cpp:managed-cpu", runtime.RecommendedFallback);
        Assert.Contains("ollama:verbeam-mort-qwen2.5-0.5b:latest", runtime.RecommendedFallback);
        Assert.Equal(2048, profile.ContextSize);
        Assert.Equal(1, profile.Parallel);
        Assert.Equal(999, profile.GpuLayers);
        Assert.Null(profile.CacheTypeK);
        Assert.Null(profile.CacheTypeV);
        Assert.Null(profile.FlashAttention);
        Assert.Equal(0, runtime.Sampling.Temperature);
        Assert.Equal(128, runtime.Sampling.MaxTokens);
    }

    [Fact]
    public void BuiltInCatalog_DeclaresGemmaFitLlamaCppProfile()
    {
        var catalog = LoadModelCatalog();
        var model = Assert.Single(catalog.Models, item => item.Id == "gemmafit-gemma4-e2b-iq2m");
        var runtime = Assert.IsType<ModelLlamaCppRuntime>(model.Runtimes.LlamaCpp);
        var profile = Assert.Single(runtime.Profiles, item => item.Name == "realtime-ocr");

        Assert.Equal("GemmaFit Gemma4 E2B IQ2_M", model.DisplayName);
        Assert.Equal("UD-IQ2_M", model.Artifact?.Quant);
        Assert.Equal("gemma-4-E2B-it-UD-IQ2_M.gguf", model.Artifact?.Filename);
        Assert.Equal("60f84cb5b9512175f219506da4a5d98d30b112855c474a3a6f06f6596dc7fd9b", model.Artifact?.Sha256);
        Assert.Equal(2290858112, model.Artifact?.SizeBytes);
        Assert.Equal("Gemma Terms of Use", model.Artifact?.License);
        Assert.Contains(
            @"D:\GemmaFit\models\gemma4_e2b_vision_gguf\gemma-4-E2B-it-UD-IQ2_M.gguf",
            model.Artifact?.LocalPaths ?? []);
        Assert.Equal("b9590", runtime.MinLlamaCppVersion);
        Assert.Equal("vulkan", runtime.BinaryFlavor);
        Assert.Equal(2048, profile.ContextSize);
        Assert.Equal(1, profile.Parallel);
        Assert.Equal(999, profile.GpuLayers);
        Assert.True(profile.Fit.HasValue);
        Assert.False(profile.Fit.Value);
        Assert.Equal("off", profile.Reasoning);
        // Device selection is now dynamic (per backend, via --list-devices), so the
        // profile no longer hardcodes a machine-specific Vulkan device index.
        Assert.False(profile.Environment.ContainsKey("GGML_VK_VISIBLE_DEVICES"));
        Assert.Null(profile.CacheTypeK);
        Assert.Null(profile.CacheTypeV);
        // Realtime speed tuning (2026-06-13): flash-attn + cache-reuse on, output
        // capped at subtitle length. See region-capture-strategy / model notes.
        Assert.True(profile.FlashAttention);
        Assert.Equal(256, profile.CacheReuse);
        Assert.Equal(0, runtime.Sampling.Temperature);
        Assert.Equal(64, runtime.Sampling.MaxTokens);
    }

    [Fact]
    public void ModelCatalogStore_RejectsAutoInLlamaCppRuntime()
    {
        var catalog = LoadModelCatalog();
        var model = catalog.Models.First(item => item.Id == "verbeam-mort-qwen2.5-0.5b");
        var badModel = model with
        {
            Runtimes = model.Runtimes with
            {
                LlamaCpp = model.Runtimes.LlamaCpp! with { BinaryFlavor = "auto" }
            }
        };
        var badCatalog = catalog with { Models = [badModel] };

        var error = Assert.Throws<InvalidOperationException>(() => ModelCatalogStore.Validate(badCatalog));
        Assert.Contains("auto", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelCatalogStore_RejectsAutoInLlamaCppBinaryArtifact()
    {
        var archive = BuildZip("bin/llama-server.exe", "server");
        var catalog = CreateTinyCatalog(System.Text.Encoding.UTF8.GetBytes("tiny gguf payload")) with
        {
            LlamaCppBinaries = [CreateBinaryArtifact(archive) with { Flavor = "auto" }]
        };

        var error = Assert.Throws<InvalidOperationException>(() => ModelCatalogStore.Validate(catalog));
        Assert.Contains("auto", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelCatalogStore_RejectsInvalidRecommendedFallback()
    {
        var catalog = LoadModelCatalog();
        var model = catalog.Models.First(item => item.Id == "verbeam-mort-qwen2.5-0.5b");
        var badModel = model with
        {
            Runtimes = model.Runtimes with
            {
                LlamaCpp = model.Runtimes.LlamaCpp! with
                {
                    RecommendedFallback = ["auto"]
                }
            }
        };
        var badCatalog = catalog with { Models = [badModel] };

        var error = Assert.Throws<InvalidOperationException>(() => ModelCatalogStore.Validate(badCatalog));
        Assert.Contains("recommendedFallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildManagedArguments_UsesSafeRealtimeDefaults()
    {
        var catalog = LoadModelCatalog();
        var model = catalog.Models.First(item => item.Id == "verbeam-mort-qwen2.5-0.5b");
        var runtime = model.Runtimes.LlamaCpp!;
        var profile = runtime.Profiles.Single(item => item.Name == "realtime-ocr");

        var args = LlamaCppRuntimeManager.BuildManagedArguments(
            model,
            runtime,
            profile,
            "D:\\models\\qwen.gguf",
            runtime.ModelAlias,
            8088,
            "D:\\slots");

        Assert.Contains("--ctx-size", args);
        Assert.Contains("2048", args);
        Assert.Contains("--parallel", args);
        Assert.Contains("1", args);
        Assert.Contains("--n-gpu-layers", args);
        Assert.Contains("999", args);
        Assert.Contains("--slot-save-path", args);
        Assert.DoesNotContain("--fit", args);
        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
        Assert.DoesNotContain("--flash-attn", args);
        Assert.DoesNotContain(args, item => item.Equals("auto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildManagedArguments_DisablesFitForGemmaFitProfile()
    {
        var catalog = LoadModelCatalog();
        var model = catalog.Models.First(item => item.Id == "gemmafit-gemma4-e2b-iq2m");
        var runtime = model.Runtimes.LlamaCpp!;
        var profile = runtime.Profiles.Single(item => item.Name == "realtime-ocr");

        var args = LlamaCppRuntimeManager.BuildManagedArguments(
            model,
            runtime,
            profile,
            "D:\\models\\gemma.gguf",
            runtime.ModelAlias,
            8088,
            "D:\\slots");

        var fitIndex = args.ToList().FindIndex(item => item == "--fit");
        Assert.True(fitIndex >= 0);
        Assert.Equal("off", args[fitIndex + 1]);
        var reasoningIndex = args.ToList().FindIndex(item => item == "--reasoning");
        Assert.True(reasoningIndex >= 0);
        Assert.Equal("off", args[reasoningIndex + 1]);
        var env = LlamaCppRuntimeManager.BuildManagedEnvironment(profile);
        // No static device pin; ResolveDeviceEnvironment sets it dynamically at launch.
        Assert.False(env.ContainsKey("GGML_VK_VISIBLE_DEVICES"));
        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
        // Realtime speed tuning (2026-06-13): flash-attn + cache-reuse enabled.
        // --flash-attn must carry a value ("on") or b9590 misparses the next arg.
        var flashIndex = args.ToList().FindIndex(item => item == "--flash-attn");
        Assert.True(flashIndex >= 0);
        Assert.Equal("on", args[flashIndex + 1]);
        var cacheReuseIndex = args.ToList().FindIndex(item => item == "--cache-reuse");
        Assert.True(cacheReuseIndex >= 0);
        Assert.Equal("256", args[cacheReuseIndex + 1]);
        Assert.DoesNotContain(args, item => item.Equals("auto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindAvailablePort_SkipsPortConflicts()
    {
        using var listener = ReservePortWithFreeSuccessor(out var occupied);
        var selected = LlamaCppRuntimeManager.FindAvailablePort(occupied, occupied + 1);

        Assert.Equal(occupied + 1, selected);
    }

    [Fact]
    public void ShouldStopForIdle_UsesClampedIdleTimeout()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(LlamaCppRuntimeManager.ShouldStopForIdle(now, now.AddSeconds(-29), idleTimeoutSeconds: 1));
        Assert.True(LlamaCppRuntimeManager.ShouldStopForIdle(now, now.AddSeconds(-31), idleTimeoutSeconds: 1));
    }

    [Fact]
    public async Task LlamaCppProvider_SendsDeterministicLowLatencyRequest()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""{"choices":[{"message":{"role":"assistant","content":"translated"}}],"timings":{"prompt_per_second":120.5,"predicted_per_second":48.25}}""")
        });
        var provider = CreateProvider(handler, out var manager);
        using (manager)
        {
            var result = await provider.TranslateAsync(BuildRequest(), CancellationToken.None);

            Assert.Equal("translated", result.Text);
            Assert.Equal("llama-cpp:verbeam-mort-qwen2.5-0.5b", result.Engine);
            Assert.Equal(120.5, result.Timings["prompt_per_second"]);
            Assert.Equal(48.25, result.Timings["predicted_per_second"]);
            var body = handler.Requests.Single();
            Assert.Equal("verbeam-mort-qwen2.5-0.5b", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.Equal(0, body.GetProperty("temperature").GetDouble());
            Assert.Equal(128, body.GetProperty("max_tokens").GetInt32());
            Assert.True(body.GetProperty("cache_prompt").GetBoolean());

            var messages = body.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Contains("Glossary:", messages[0].GetProperty("content").GetString());
            Assert.DoesNotContain("volatile memory context", messages[0].GetProperty("content").GetString());
            Assert.Contains("volatile memory context", messages[1].GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task LlamaCppProvider_RetriesWithoutCachePromptWhenEndpointRejectsIt()
    {
        var attempt = 0;
        using var handler = new CapturingHandler(_ =>
        {
            attempt++;
            return attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent("""{"error":"unknown field cache_prompt"}""")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"choices":[{"message":{"role":"assistant","content":"translated"}}]}""")
                };
        });
        var provider = CreateProvider(handler, out var manager);
        using (manager)
        {
            var result = await provider.TranslateAsync(BuildRequest(), CancellationToken.None);

            Assert.Equal("translated", result.Text);
            Assert.Equal(2, handler.Requests.Count);
            Assert.True(handler.Requests[0].TryGetProperty("cache_prompt", out _));
            Assert.False(handler.Requests[1].TryGetProperty("cache_prompt", out _));
            Assert.False(manager.GetStatus().CachePromptEnabled);
        }
    }

    [Fact]
    public async Task LlamaCppArtifactStore_DownloadsAndVerifiesArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-artifact-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny gguf payload");
            var catalogService = await CreateCatalogServiceAsync(CreateTinyCatalog(payload), root);
            using var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            var store = new LlamaCppArtifactStore(
                new LlamaCppOptions { ModelsDirectory = "models/llama-cpp", Model = "tiny-model" },
                catalogService,
                root,
                new HttpClient(handler));

            var before = await store.GetStatusAsync("tiny-model");
            Assert.False(before.Exists);
            Assert.False(before.Verified);

            var result = await store.EnsureModelAsync("tiny-model");
            Assert.True(result.Verified);
            Assert.Equal(payload.Length, result.BytesWritten);
            Assert.True(File.Exists(result.LocalPath));

            var after = await store.GetStatusAsync("tiny-model");
            Assert.True(after.Exists);
            Assert.True(after.SizeMatches);
            Assert.True(after.Sha256Matches);
            Assert.True(after.Verified);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppArtifactStore_RejectsHashMismatchAndDeletesTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-artifact-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny gguf payload");
            var badCatalog = CreateTinyCatalog(payload) with
            {
                Models =
                [
                    CreateTinyCatalog(payload).Models.Single() with
                    {
                        Artifact = CreateTinyCatalog(payload).Models.Single().Artifact! with
                        {
                            Sha256 = new string('0', 64)
                        }
                    }
                ]
            };
            var catalogService = await CreateCatalogServiceAsync(badCatalog, root);
            using var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            var store = new LlamaCppArtifactStore(
                new LlamaCppOptions { ModelsDirectory = "models/llama-cpp", Model = "tiny-model" },
                catalogService,
                root,
                new HttpClient(handler));
            var localPath = store.ResolveLocalPath(catalogService.GetCurrent().Models.Single());

            await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureModelAsync("tiny-model"));

            Assert.False(File.Exists(localPath));
            Assert.False(File.Exists(localPath + ".download"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppBinaryStore_DownloadsVerifiesAndExtractsPinnedBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-binary-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var archive = BuildZip("bin/llama-server.exe", "server");
            var catalogService = await CreateCatalogServiceAsync(
                CreateTinyCatalog(System.Text.Encoding.UTF8.GetBytes("tiny gguf payload")) with
                {
                    LlamaCppBinaries = [CreateBinaryArtifact(archive)]
                },
                root);
            using var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
            var store = new LlamaCppBinaryStore(
                new LlamaCppOptions
                {
                    BinariesDirectory = "runtimes/llama-cpp",
                    PinnedVersion = "b9590",
                    BinaryFlavor = "vulkan"
                },
                catalogService,
                root,
                new HttpClient(handler));

            var result = await store.EnsureBinaryAsync();

            Assert.True(result.Ready);
            Assert.Equal("vulkan", result.Flavor);
            Assert.True(File.Exists(result.ExecutablePath));
            Assert.Equal("server", await File.ReadAllTextAsync(result.ExecutablePath));
            var statuses = await store.GetStatusesAsync();
            var status = Assert.Single(statuses);
            Assert.True(status.ArchiveExists);
            Assert.True(status.ArchiveSizeMatches);
            Assert.True(status.ArchiveSha256Matches);
            Assert.True(status.ExecutableExists);
            Assert.True(status.Ready);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppBinaryStore_FallsBackToCpuArtifactWhenPreferredFlavorIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-binary-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var archive = BuildZip("bin/llama-server.exe", "cpu-server");
            var catalogService = await CreateCatalogServiceAsync(
                CreateTinyCatalog(System.Text.Encoding.UTF8.GetBytes("tiny gguf payload")) with
                {
                    LlamaCppBinaries = [CreateBinaryArtifact(archive) with { Flavor = "cpu" }]
                },
                root);
            using var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
            var store = new LlamaCppBinaryStore(
                new LlamaCppOptions
                {
                    BinariesDirectory = "runtimes/llama-cpp",
                    PinnedVersion = "b9590",
                    BinaryFlavor = "vulkan"
                },
                catalogService,
                root,
                new HttpClient(handler));

            var result = await store.EnsureBinaryAsync();

            Assert.True(result.Ready);
            Assert.Equal("cpu", result.Flavor);
            Assert.Equal("cpu-server", await File.ReadAllTextAsync(result.ExecutablePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppBinaryStore_RejectsHashMismatchAndDeletesTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-binary-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var archive = BuildZip("bin/llama-server.exe", "server");
            var badBinary = CreateBinaryArtifact(archive) with { Sha256 = new string('0', 64) };
            var catalogService = await CreateCatalogServiceAsync(
                CreateTinyCatalog(System.Text.Encoding.UTF8.GetBytes("tiny gguf payload")) with
                {
                    LlamaCppBinaries = [badBinary]
                },
                root);
            using var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
            var store = new LlamaCppBinaryStore(
                new LlamaCppOptions
                {
                    BinariesDirectory = "runtimes/llama-cpp",
                    PinnedVersion = "b9590",
                    BinaryFlavor = "vulkan"
                },
                catalogService,
                root,
                new HttpClient(handler));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureBinaryAsync());

            var status = Assert.Single(await store.GetStatusesAsync());
            Assert.False(File.Exists(status.ArchivePath + ".download"));
            Assert.False(status.ArchiveExists);
            Assert.False(status.ExecutableExists);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppInstallService_RemoteModeSelectsProviderWithoutDownloading()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-install-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny gguf payload");
            var catalogService = await CreateCatalogServiceAsync(CreateTinyCatalog(payload), root);
            var options = new VerbeamOptions
            {
                DefaultProvider = "ollama",
                LlamaCpp = new LlamaCppOptions
                {
                    Mode = "remote",
                    BaseUrl = "http://llama.test/v1",
                    Model = "tiny-model",
                    ModelsDirectory = "models/llama-cpp",
                    BinariesDirectory = "runtimes/llama-cpp"
                }
            };
            using var artifactHandler = new RespondingHandler(_ => throw new InvalidOperationException("unexpected artifact download"));
            using var binaryHandler = new RespondingHandler(_ => throw new InvalidOperationException("unexpected binary download"));
            var artifactStore = new LlamaCppArtifactStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(artifactHandler));
            var binaryStore = new LlamaCppBinaryStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(binaryHandler));
            using var manager = new LlamaCppRuntimeManager(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient());
            var installer = new LlamaCppInstallService(
                options,
                catalogService,
                artifactStore,
                binaryStore,
                manager);

            var result = await installer.InstallAndUseAsync(new LlamaCppInstallRequest
            {
                ModelId = "tiny-model",
                Mode = "remote",
                StartServer = true
            });

            Assert.Equal("llama-cpp", result.Provider);
            Assert.Equal("remote", result.Mode);
            Assert.True(result.Ready);
            Assert.False(result.StartedServer);
            Assert.Null(result.Artifact);
            Assert.Null(result.Binary);
            Assert.Equal("llama-cpp", options.DefaultProvider);
            Assert.Equal("tiny-model", options.LlamaCpp.Model);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppInstallService_ManagedModeDownloadsArtifactsAndBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-install-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny gguf payload");
            var archive = BuildZip("bin/llama-server.exe", "server");
            var catalogService = await CreateCatalogServiceAsync(
                CreateTinyCatalog(payload) with
                {
                    LlamaCppBinaries = [CreateBinaryArtifact(archive)]
                },
                root);
            var options = new VerbeamOptions
            {
                DefaultProvider = "ollama",
                LlamaCpp = new LlamaCppOptions
                {
                    Mode = "remote",
                    Model = "tiny-model",
                    ModelsDirectory = "models/llama-cpp",
                    BinariesDirectory = "runtimes/llama-cpp",
                    PinnedVersion = "b9590",
                    BinaryFlavor = "vulkan"
                }
            };
            using var artifactHandler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            using var binaryHandler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
            var artifactStore = new LlamaCppArtifactStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(artifactHandler));
            var binaryStore = new LlamaCppBinaryStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(binaryHandler));
            using var manager = new LlamaCppRuntimeManager(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(),
                artifactStore,
                binaryStore);
            var installer = new LlamaCppInstallService(
                options,
                catalogService,
                artifactStore,
                binaryStore,
                manager);

            var result = await installer.InstallAndUseAsync(new LlamaCppInstallRequest
            {
                ModelId = "tiny-model",
                Mode = "managed",
                StartServer = false
            });

            Assert.Equal("managed", result.Mode);
            Assert.True(result.Ready);
            Assert.False(result.StartedServer);
            Assert.NotNull(result.Artifact);
            Assert.NotNull(result.Binary);
            Assert.True(result.Artifact.Verified);
            Assert.True(result.Binary.Ready);
            Assert.True(File.Exists(result.Artifact.LocalPath));
            Assert.True(File.Exists(result.Binary.ExecutablePath));
            Assert.Equal("llama-cpp", options.DefaultProvider);
            Assert.Equal("managed", options.LlamaCpp.Mode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppArtifactStore_ImportsVerifiedLocalArtifactBeforeDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-local-artifact-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny local gguf payload");
            var sourceDirectory = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "tiny.gguf");
            await File.WriteAllBytesAsync(sourcePath, payload);

            var catalog = CreateTinyCatalog(payload);
            var model = catalog.Models[0];
            var localCatalog = catalog with
            {
                Models =
                [
                    model with
                    {
                        Artifact = model.Artifact! with
                        {
                            DownloadUrl = "",
                            LocalPaths = [sourcePath]
                        }
                    }
                ]
            };
            var catalogService = await CreateCatalogServiceAsync(localCatalog, root);
            var options = new LlamaCppOptions
            {
                Model = "tiny-model",
                ModelsDirectory = "models/llama-cpp"
            };
            using var artifactHandler = new RespondingHandler(_ =>
                throw new InvalidOperationException("Download should not be used when a verified local artifact exists."));
            var artifactStore = new LlamaCppArtifactStore(
                options,
                catalogService,
                root,
                new HttpClient(artifactHandler));

            var status = Assert.Single(await artifactStore.GetStatusesAsync(verifySha256: false));
            Assert.True(status.Exists);
            Assert.True(status.SizeMatches);
            Assert.Equal(sourcePath, status.LocalPath);

            var result = await artifactStore.EnsureModelAsync("tiny-model");

            Assert.True(result.Verified);
            Assert.True(File.Exists(result.LocalPath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.LocalPath));
            Assert.Equal(sourcePath, result.LocalPath);
            Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppInstallService_ManagedModeFallsBackFromVulkanToCpu()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-install-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny gguf payload");
            var archive = BuildZip("bin/llama-server.exe", "cpu-server");
            var catalogService = await CreateCatalogServiceAsync(
                CreateTinyCatalog(payload) with
                {
                    LlamaCppBinaries =
                    [
                        CreateBinaryArtifact(archive) with
                        {
                            Flavor = "vulkan",
                            DownloadUrl = "http://artifact.test/vulkan.zip",
                            Filename = "vulkan.zip"
                        },
                        CreateBinaryArtifact(archive) with
                        {
                            Flavor = "cpu",
                            DownloadUrl = "http://artifact.test/cpu.zip",
                            Filename = "cpu.zip"
                        }
                    ]
                },
                root);
            var options = new VerbeamOptions
            {
                LlamaCpp = new LlamaCppOptions
                {
                    Mode = "remote",
                    Model = "tiny-model",
                    ModelsDirectory = "models/llama-cpp",
                    BinariesDirectory = "runtimes/llama-cpp",
                    PinnedVersion = "b9590",
                    BinaryFlavor = "vulkan"
                }
            };
            using var artifactHandler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            using var binaryHandler = new RespondingHandler(request =>
                request.RequestUri?.AbsoluteUri.Contains("vulkan", StringComparison.OrdinalIgnoreCase) == true
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archive)
                    });
            var artifactStore = new LlamaCppArtifactStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(artifactHandler));
            var binaryStore = new LlamaCppBinaryStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(binaryHandler));
            using var manager = new LlamaCppRuntimeManager(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(),
                artifactStore,
                binaryStore);
            var installer = new LlamaCppInstallService(
                options,
                catalogService,
                artifactStore,
                binaryStore,
                manager);

            var result = await installer.InstallAndUseAsync(new LlamaCppInstallRequest
            {
                ModelId = "tiny-model",
                Mode = "managed",
                StartServer = false
            });

            Assert.True(result.Ready);
            Assert.NotNull(result.Binary);
            Assert.Equal("cpu", result.Binary.Flavor);
            Assert.Equal("cpu", options.LlamaCpp.BinaryFlavor);
            Assert.Equal("cpu-server", await File.ReadAllTextAsync(result.Binary.ExecutablePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppRuntimeSettingsStore_RoundTripsAndApplies()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "data", "llama-cpp-runtime.json");
            var store = new LlamaCppRuntimeSettingsStore(path);

            var fresh = new LlamaCppOptions();
            Assert.False(await store.ApplyAsync(fresh));
            Assert.Equal("remote", fresh.Mode);

            await store.SaveAsync(new LlamaCppOptions
            {
                Mode = "managed",
                Model = "tiny-model",
                BinaryFlavor = "cpu",
                PinnedVersion = "b9591"
            });

            var restarted = new LlamaCppOptions();
            Assert.True(await store.ApplyAsync(restarted));
            Assert.Equal("managed", restarted.Mode);
            Assert.Equal("tiny-model", restarted.Model);
            Assert.Equal("cpu", restarted.BinaryFlavor);
            Assert.Equal("b9591", restarted.PinnedVersion);

            await File.WriteAllTextAsync(path, "{ not json");
            var corrupted = new LlamaCppOptions();
            Assert.False(await store.ApplyAsync(corrupted));
            Assert.Equal("remote", corrupted.Mode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppInstallService_PersistsRuntimeSelectionAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-llama-install-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("tiny gguf payload");
            var archive = BuildZip("bin/llama-server.exe", "server");
            var catalogService = await CreateCatalogServiceAsync(
                CreateTinyCatalog(payload) with
                {
                    LlamaCppBinaries = [CreateBinaryArtifact(archive)]
                },
                root);
            var options = new VerbeamOptions
            {
                LlamaCpp = new LlamaCppOptions
                {
                    Mode = "remote",
                    Model = "tiny-model",
                    ModelsDirectory = "models/llama-cpp",
                    BinariesDirectory = "runtimes/llama-cpp",
                    PinnedVersion = "b9590",
                    BinaryFlavor = "vulkan"
                }
            };
            using var artifactHandler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            using var binaryHandler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
            var artifactStore = new LlamaCppArtifactStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(artifactHandler));
            var binaryStore = new LlamaCppBinaryStore(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(binaryHandler));
            using var manager = new LlamaCppRuntimeManager(
                options.LlamaCpp,
                catalogService,
                root,
                new HttpClient(),
                artifactStore,
                binaryStore);
            var settingsStore = new LlamaCppRuntimeSettingsStore(Path.Combine(root, "data", "llama-cpp-runtime.json"));
            var installer = new LlamaCppInstallService(
                options,
                catalogService,
                artifactStore,
                binaryStore,
                manager,
                settingsStore);

            var result = await installer.InstallAndUseAsync(new LlamaCppInstallRequest
            {
                ModelId = "tiny-model",
                Mode = "managed",
                StartServer = false
            });
            Assert.True(result.Ready);

            // Simulate a restart: defaults come back as remote, then the persisted choice is applied.
            var restarted = new LlamaCppOptions();
            Assert.True(await settingsStore.ApplyAsync(restarted));
            Assert.Equal("managed", restarted.Mode);
            Assert.Equal("tiny-model", restarted.Model);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LlamaCppProvider_ConnectionFailureProducesActionableError()
    {
        using var handler = new CapturingHandler(_ => throw new HttpRequestException("連線被拒 (localhost:8088)"));
        var provider = CreateProvider(handler, out var manager);
        using (manager)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.TranslateAsync(BuildRequest(), CancellationToken.None));

            Assert.Contains("Cannot reach llama.cpp server", error.Message);
            Assert.Contains("http://llama.test/v1", error.Message);
            Assert.Contains("Install and Use", error.Message);
            Assert.Contains("remote mode", error.Message);
        }
    }

    [Fact]
    public async Task WarmUpAsync_RemoteModeIsNoOp()
    {
        var options = new LlamaCppOptions
        {
            Mode = "remote",
            BaseUrl = "http://llama.test/v1",
            Model = "verbeam-mort-qwen2.5-0.5b"
        };
        using var manager = new LlamaCppRuntimeManager(
            options,
            CreateCatalogService(),
            AppContext.BaseDirectory,
            new HttpClient());

        // Remote mode has no managed server to start and must not reach out to the
        // network; warmup returns immediately without throwing.
        await manager.WarmUpAsync(CancellationToken.None);

        var status = manager.GetStatus();
        Assert.False(status.IsManagedRunning);
        Assert.Equal(string.Empty, status.LastError);
    }

    private static LlamaCppTranslationProvider CreateProvider(
        CapturingHandler handler,
        out LlamaCppRuntimeManager manager)
    {
        var options = new VerbeamOptions
        {
            LlamaCpp = new LlamaCppOptions
            {
                Mode = "remote",
                BaseUrl = "http://llama.test/v1",
                Model = "verbeam-mort-qwen2.5-0.5b"
            }
        };
        manager = new LlamaCppRuntimeManager(
            options.LlamaCpp,
            CreateCatalogService(),
            AppContext.BaseDirectory,
            new HttpClient());
        return new LlamaCppTranslationProvider(new HttpClient(handler), options.LlamaCpp, manager);
    }

    private static ProviderTranslationRequest BuildRequest()
        => new(
            "source text",
            "ja",
            "zh-TW",
            "game_dialogue",
            "verbeam-mort-qwen2.5-0.5b",
            new PromptPreset
            {
                Id = "game_dialogue",
                Name = "Game Dialogue",
                SystemPrompt = "Translate carefully.",
                UserTemplate = "Translate {TEXT}."
            },
            new Dictionary<string, string>
            {
                ["Star Key"] = "星之鑰"
            },
            "volatile request context",
            "volatile memory context");

    private static ModelCatalogDocument LoadModelCatalog()
    {
        var catalogPath = PathResolver.Resolve(AppContext.BaseDirectory, "models.catalog.json");
        return new ModelCatalogStore(catalogPath).LoadAsync().GetAwaiter().GetResult();
    }

    private static ModelCatalogService CreateCatalogService()
    {
        var catalogPath = PathResolver.Resolve(AppContext.BaseDirectory, "models.catalog.json");
        var service = new ModelCatalogService(
            catalogPath,
            Path.Combine(Path.GetTempPath(), "verbeam-model-catalog-test-" + Guid.NewGuid() + ".json"),
            new ModelCatalogOptions(),
            new HttpClient());
        service.InitializeAsync().GetAwaiter().GetResult();
        return service;
    }

    private static async Task<ModelCatalogService> CreateCatalogServiceAsync(
        ModelCatalogDocument catalog,
        string root)
    {
        var catalogPath = Path.Combine(root, "models.catalog.json");
        await new ModelCatalogStore(catalogPath).SaveAsync(catalog);
        var service = new ModelCatalogService(
            catalogPath,
            Path.Combine(root, "models.catalog.cache.json"),
            new ModelCatalogOptions(),
            new HttpClient());
        await service.InitializeAsync();
        return service;
    }

    private static ModelCatalogDocument CreateTinyCatalog(byte[] payload)
        => new()
        {
            SchemaVersion = 2,
            CatalogVersion = "2026-06-11-test",
            Models =
            [
                new ModelCatalogEntry
                {
                    Id = "tiny-model",
                    Provider = "ollama",
                    Name = "tiny-model",
                    DisplayName = "Tiny Model",
                    Tier = "tiny",
                    RecommendedUse = "tests",
                    EstimatedMemoryGb = 1,
                    QualityScore = 0.5,
                    LatencyScore = 0.9,
                    ContextScore = 0.5,
                    StabilityScore = 0.9,
                    Install = new ModelCatalogInstallPlan { Type = "manual" },
                    Artifact = new ModelArtifact
                    {
                        Format = "gguf",
                        Quant = "Q8_0",
                        DownloadUrl = "http://artifact.test/tiny.gguf",
                        HuggingFace = "artifact.test/tiny",
                        Filename = "tiny.gguf",
                        Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                        SizeBytes = payload.Length,
                        License = "Apache-2.0"
                    },
                    Runtimes = new ModelRuntimeSet
                    {
                        LlamaCpp = new ModelLlamaCppRuntime
                        {
                            ModelAlias = "tiny-model",
                            MinLlamaCppVersion = "b9590",
                            BinaryFlavor = "vulkan",
                            RecommendedFallback = ["llama-cpp:managed-cpu"],
                            Profiles = [new ModelLlamaCppProfile()]
                        }
                    }
                }
            ]
        };

    private static LlamaCppBinaryArtifact CreateBinaryArtifact(byte[] archive)
        => new()
        {
            Version = "b9590",
            Flavor = "vulkan",
            Platform = CurrentPlatform(),
            Architecture = CurrentArchitecture(),
            DownloadUrl = "http://artifact.test/llama.cpp.zip",
            Filename = "llama.cpp.zip",
            Sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
            SizeBytes = archive.Length,
            ExecutableRelativePath = "bin/llama-server.exe"
        };

    private static byte[] BuildZip(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return stream.ToArray();
    }

    private static TcpListener ReservePortWithFreeSuccessor(out int occupied)
    {
        for (var port = 45000; port < 55000; port++)
        {
            TcpListener? first = null;
            TcpListener? second = null;
            try
            {
                first = new TcpListener(IPAddress.Loopback, port);
                first.Start();
                second = new TcpListener(IPAddress.Loopback, port + 1);
                second.Start();
                second.Stop();
                occupied = port;
                return first;
            }
            catch (SocketException)
            {
                first?.Stop();
                second?.Stop();
            }
        }

        throw new InvalidOperationException("Could not reserve adjacent test ports.");
    }

    private static string CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return RuntimeInformation.OSDescription;
    }

    private static string CurrentArchitecture()
        => RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };

    private static StringContent JsonContent(string json)
        => new(json, System.Text.Encoding.UTF8, "application/json");

    private sealed class CapturingHandler : HttpMessageHandler, IDisposable
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(JsonDocument.Parse(body).RootElement.Clone());
            return _respond(request);
        }
    }

    private sealed class RespondingHandler : HttpMessageHandler, IDisposable
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
