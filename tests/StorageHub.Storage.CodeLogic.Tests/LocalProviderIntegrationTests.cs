using CL.Storage;
using CL.Storage.Configuration;
using CodeLogic;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.CodeLogic.Tests;

[Collection(ProviderIntegrationFixtureGroup.Name)]
public sealed class LocalProviderIntegrationTests
{
    [Fact]
    public async Task RuntimeOnlyLocalConnection_WritesListsAndReadsThroughRealCodeLogicStorage()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"storagehub-cl-storage-{Guid.NewGuid():N}");
        var providerRoot = Path.Combine(testRoot, "provider");
        Directory.CreateDirectory(providerRoot);
        var orphan = Path.Combine(
            providerRoot,
            ".storagehub-internal",
            "staging",
            $"{new string('a', 64)}.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
        try
        {
            var initialization = await global::CodeLogic.CodeLogic.InitializeAsync(options =>
            {
                options.FrameworkRootPath = Path.Combine(testRoot, "framework");
                options.ApplicationRootPath = Path.Combine(testRoot, "application");
                options.AppVersion = "test";
                options.HandleShutdownSignals = false;
            });
            Assert.True(initialization.Success);

            await Libraries.LoadAsync<StorageLibrary>();
            Libraries.OverrideConfig<StorageConfig>(
                "CL.Storage",
                "storage",
                configuration => configuration.Enabled = false);
            await global::CodeLogic.CodeLogic.ConfigureAsync();
            await global::CodeLogic.CodeLogic.StartAsync();

            var profileId = ConnectionProfileId.New();
            const string rootIdentity = "integration-root-v1";
            var library = Libraries.Get<StorageLibrary>() ??
                throw new InvalidOperationException("CL.Storage was not registered by CodeLogic.");
            var factory = new CodeLogicStorageSessionFactory(library);
            var registration = await factory.RegisterLocalAsync(
                profileId,
                rootIdentity,
                new LocalConnectionConfig { RootPath = providerRoot, Enabled = true });
            Assert.True(registration.IsSuccess);
            Assert.False(File.Exists(orphan));

            await using var connection = registration.Value;
            var rootAddress = StorageAddress.Create(profileId, rootIdentity, string.Empty).Value;
            var rootListing = await connection.Session.ListAsync(rootAddress);
            Assert.True(rootListing.IsSuccess);
            Assert.DoesNotContain(rootListing.Value.Entries, entry =>
                entry.Name.Equals(".storagehub-internal", StringComparison.OrdinalIgnoreCase));
            var reservedAddress = StorageAddress.Create(
                profileId,
                rootIdentity,
                ".storagehub-internal/staging").Value;
            var reservedLookup = await connection.Session.GetEntryAsync(reservedAddress);
            Assert.True(reservedLookup.IsFailure);
            Assert.Equal("storage.path.reserved", reservedLookup.Error.Code);

            var destination = StorageAddress.Create(profileId, rootIdentity, "folder/payload.bin").Value;
            var payload = Enumerable.Range(0, 180_000).Select(index => (byte)(index % 241)).ToArray();
            var opened = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                destination,
                StorageWriteMode.CreateNew,
                payload.LongLength));
            Assert.True(opened.IsSuccess);

            await using (var handle = opened.Value)
            {
                await handle.Content.WriteAsync(payload);
                var committed = await handle.CommitAsync();
                Assert.True(committed.IsSuccess);
                Assert.Equal(payload.LongLength, committed.Value.Size);
            }

            Assert.Empty(Directory.EnumerateFiles(
                providerRoot,
                "*.partial",
                SearchOption.AllDirectories));

            var abortedDestination = StorageAddress.Create(
                profileId,
                rootIdentity,
                "folder/aborted.bin").Value;
            var abortedOpen = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                abortedDestination,
                StorageWriteMode.CreateNew));
            Assert.True(abortedOpen.IsSuccess);
            await using (var abortedHandle = abortedOpen.Value)
            {
                await abortedHandle.Content.WriteAsync(payload.AsMemory(0, 1024));
                var aborted = await abortedHandle.AbortAsync();
                Assert.True(aborted.IsSuccess);
            }

            Assert.False(File.Exists(Path.Combine(providerRoot, "folder", "aborted.bin")));

            var mismatchedDestination = StorageAddress.Create(
                profileId,
                rootIdentity,
                "folder/mismatched.bin").Value;
            var mismatchedOpen = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                mismatchedDestination,
                StorageWriteMode.CreateNew,
                expectedLength: 2));
            Assert.True(mismatchedOpen.IsSuccess);
            await using (var mismatchedHandle = mismatchedOpen.Value)
            {
                await mismatchedHandle.Content.WriteAsync(new byte[] { 1 });
                var mismatched = await mismatchedHandle.CommitAsync();
                Assert.True(mismatched.IsFailure);
                Assert.Equal("storage.write.length_mismatch", mismatched.Error.Code);
            }

            Assert.False(File.Exists(Path.Combine(providerRoot, "folder", "mismatched.bin")));

            var existingPath = Path.Combine(providerRoot, "folder", "existing.bin");
            var original = new byte[] { 9, 8, 7 };
            await File.WriteAllBytesAsync(existingPath, original);
            var existingDestination = StorageAddress.Create(
                profileId,
                rootIdentity,
                "folder/existing.bin").Value;
            var conflictingOpen = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                existingDestination,
                StorageWriteMode.CreateNew,
                expectedLength: 1));
            Assert.True(conflictingOpen.IsSuccess);
            await using (var conflictingHandle = conflictingOpen.Value)
            {
                await conflictingHandle.Content.WriteAsync(new byte[] { 5 });
                var conflicting = await conflictingHandle.CommitAsync();
                Assert.True(conflicting.IsFailure);
                Assert.Equal(StorageHub.Contracts.Results.StorageFailureKind.Conflict, conflicting.Error.Kind);
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(existingPath));

            var overwriteDestination = StorageAddress.Create(
                profileId,
                rootIdentity,
                "folder/overwrite.bin").Value;
            var overwritePath = Path.Combine(providerRoot, "folder", "overwrite.bin");
            await File.WriteAllBytesAsync(overwritePath, original);
            var failedOverwriteOpen = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                overwriteDestination,
                StorageWriteMode.Overwrite,
                expectedLength: 2));
            Assert.True(failedOverwriteOpen.IsSuccess);
            await using (var failedOverwriteHandle = failedOverwriteOpen.Value)
            {
                await failedOverwriteHandle.Content.WriteAsync(new byte[] { 1 });
                var failedOverwrite = await failedOverwriteHandle.CommitAsync();
                Assert.True(failedOverwrite.IsFailure);
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(overwritePath));

            var abortedOverwriteOpen = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                overwriteDestination,
                StorageWriteMode.Overwrite));
            Assert.True(abortedOverwriteOpen.IsSuccess);
            await using (var abortedOverwriteHandle = abortedOverwriteOpen.Value)
            {
                await abortedOverwriteHandle.Content.WriteAsync(new byte[] { 4, 5, 6 });
                Assert.True((await abortedOverwriteHandle.AbortAsync()).IsSuccess);
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(overwritePath));

            var replacement = new byte[] { 6, 5, 4, 3 };
            var overwriteOpen = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
                overwriteDestination,
                StorageWriteMode.Overwrite,
                expectedLength: replacement.LongLength));
            Assert.True(overwriteOpen.IsSuccess);
            await using (var overwriteHandle = overwriteOpen.Value)
            {
                await overwriteHandle.Content.WriteAsync(replacement);
                Assert.True((await overwriteHandle.CommitAsync()).IsSuccess);
            }

            Assert.Equal(replacement, await File.ReadAllBytesAsync(overwritePath));
            Assert.Empty(Directory.EnumerateFiles(
                providerRoot,
                "*.partial",
                SearchOption.AllDirectories));

            var folder = StorageAddress.Create(profileId, rootIdentity, "folder").Value;
            var listing = await connection.Session.ListAsync(folder);
            Assert.True(listing.IsSuccess);
            Assert.Contains(listing.Value.Entries, entry => entry.Name == "payload.bin");

            var read = await connection.Session.OpenReadAsync(new StorageReadRequest(destination));
            Assert.True(read.IsSuccess);
            await using var source = read.Value;
            using var copied = new MemoryStream();
            await source.CopyToAsync(copied);
            Assert.Equal(payload, copied.ToArray());
        }
        finally
        {
            await global::CodeLogic.CodeLogic.StopAsync();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
