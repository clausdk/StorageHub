using System.Globalization;
using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Domain.Tests;

public sealed class IpcContractTests
{
    [Fact]
    public void Envelope_and_hello_payload_round_trip_through_json()
    {
        var requestId = Guid.Parse("39db56a0-b39c-418c-a0f4-f0814c19cfaa");
        var request = new HelloRequest(
            ProtocolVersion.Current,
            "StorageHub.Desktop",
            "1.2.3",
            Guid.Parse("12b5227b-48a6-4b74-81cc-7f52e047a41f"));
        var envelope = IpcEnvelope.Create("hello.request", requestId, sequence: 7, request);

        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<IpcEnvelope>(json)!;
        var restoredRequest = restored.DeserializePayload<HelloRequest>();

        Assert.Equal("hello.request", restored.MessageType);
        Assert.Equal(requestId, restored.RequestId);
        Assert.Equal(7, restored.Sequence);
        Assert.Equal(ProtocolVersion.Current, restoredRequest.ProtocolVersion);
        Assert.Equal("StorageHub.Desktop", restoredRequest.ClientName);
    }

    [Fact]
    public void Frame_limits_use_distinct_normal_and_secret_boundaries()
    {
        Assert.Equal(8 * 1024 * 1024, IpcFrameLimits.NormalMaxBytes);
        Assert.Equal(32 * 1024 * 1024, IpcFrameLimits.SecretMaxBytes);
        Assert.True(IpcFrameLimits.IsAllowed(IpcFrameLimits.NormalMaxBytes, IpcFrameKind.Normal));
        Assert.False(IpcFrameLimits.IsAllowed(IpcFrameLimits.NormalMaxBytes + 1L, IpcFrameKind.Normal));
        Assert.True(IpcFrameLimits.IsAllowed(IpcFrameLimits.SecretMaxBytes, IpcFrameKind.Secret));
        Assert.False(IpcFrameLimits.IsAllowed(-1, IpcFrameKind.Secret));
    }

    [Fact]
    public void Agent_status_snapshot_is_json_serializable()
    {
        var snapshot = new AgentStatusSnapshot(
            Guid.Parse("02771a4b-d1b9-44f5-b835-3ae10ce8b04c"),
            AgentLifecycleState.Ready,
            DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture),
            ActiveTransfers: 2,
            ActiveSyncRuns: 1,
            Detail: null);

        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<AgentStatusSnapshot>(json);

        Assert.Equal(snapshot, restored);
    }

    [Fact]
    public void Profile_draft_contains_only_opaque_secret_references()
    {
        const string reference = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var draft = new ConnectionProfileDraft(
            new ConnectionProfileMetadataDocument("Archive", Tags: []),
            new ConnectionEndpointDocument(
                StorageConnectionProvider.S3,
                Bucket: "archive",
                Region: "eu-north-1",
                ServiceEndpoint: "https://s3.example.com"),
            new ConnectionAuthenticationDocument(
                ConnectionAuthenticationKind.S3AccessKey,
                AccessKeyReference: reference,
                SecretKeyReference: reference),
            new ConnectionOperationalOptionsDocument());

        var json = JsonSerializer.Serialize(draft);

        Assert.True(draft.HasValidBounds);
        Assert.Contains(reference, json, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretMaterial", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordValue", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Notes", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_draft_rejects_a_non_opaque_authentication_reference()
    {
        var draft = new ConnectionProfileDraft(
            new ConnectionProfileMetadataDocument("Archive", Tags: []),
            new ConnectionEndpointDocument(
                StorageConnectionProvider.S3,
                Bucket: "archive",
                Region: "eu-north-1"),
            new ConnectionAuthenticationDocument(
                ConnectionAuthenticationKind.S3AccessKey,
                AccessKeyReference: "AKIAEXAMPLE",
                SecretKeyReference: "raw-secret"),
            new ConnectionOperationalOptionsDocument());

        Assert.False(draft.HasValidBounds);
    }

    [Fact]
    public void Ssh_key_and_password_mfa_requires_three_opaque_secret_references()
    {
        const string reference = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var authentication = new ConnectionAuthenticationDocument(
            ConnectionAuthenticationKind.SshPrivateKeyPassword,
            Username: "operator",
            PasswordReference: reference,
            PrivateKeyReference: reference,
            PrivateKeyPassphraseReference: reference);
        var draft = new ConnectionProfileDraft(
            new ConnectionProfileMetadataDocument("MFA shell", Tags: []),
            new ConnectionEndpointDocument(
                StorageConnectionProvider.Ssh,
                Host: "ssh.example.test",
                Port: 22),
            authentication,
            new ConnectionOperationalOptionsDocument(),
            Type: ConnectionProfileType.Client);

        Assert.True(authentication.HasValidBounds);
        Assert.True(draft.HasValidBounds);
        Assert.False((authentication with { PasswordReference = null }).HasValidBounds);
        Assert.False((authentication with { PrivateKeyReference = null }).HasValidBounds);
        Assert.False((draft with
        {
            Endpoint = new ConnectionEndpointDocument(
                StorageConnectionProvider.Sftp,
                Host: "sftp.example.test",
                Port: 22),
            Type = ConnectionProfileType.Storage
        }).HasValidBounds);
    }

    [Fact]
    public void Secret_request_requires_operation_specific_material_and_bounds()
    {
        const string reference = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        Assert.True(new SecretVaultRequest(
            SecretVaultIpcContract.CurrentVersion,
            SecretVaultOperation.Enroll,
            SecretMaterialPurpose.Password,
            Reference: null,
            SecretMaterial: [1]).HasValidBounds);
        Assert.True(new SecretVaultRequest(
            SecretVaultIpcContract.CurrentVersion,
            SecretVaultOperation.Delete,
            SecretMaterialPurpose.Password,
            reference,
            SecretMaterial: null).HasValidBounds);
        Assert.False(new SecretVaultRequest(
            SecretVaultIpcContract.CurrentVersion,
            SecretVaultOperation.Delete,
            SecretMaterialPurpose.Password,
            reference,
            SecretMaterial: [1]).HasValidBounds);
        Assert.False(new SecretVaultRequest(
            SecretVaultIpcContract.CurrentVersion,
            SecretVaultOperation.Enroll,
            SecretMaterialPurpose.Password,
            Reference: null,
            SecretMaterial: new byte[SecretVaultIpcContract.MaximumSecretBytes + 1]).HasValidBounds);
    }

    [Fact]
    public void TrustMutationsRequireProfileRevisionStrongFingerprintAndPairedRecordVersion()
    {
        var connectionId = Guid.NewGuid();
        var valid = new ConnectionTrustDecisionRequest(
            ConnectionTrustIpcContract.CurrentVersion,
            connectionId,
            ExpectedProfileVersion: 3,
            new string('A', 64),
            ConnectionTrustDecision.Trusted);

        Assert.True(valid.HasValidBounds);
        Assert.False((valid with { ExpectedProfileVersion = 0 }).HasValidBounds);
        Assert.False((valid with { Sha256Fingerprint = "MD5:unsafe" }).HasValidBounds);
        Assert.False((valid with { Decision = ConnectionTrustDecision.Revoked }).HasValidBounds);
        Assert.False((valid with { ExistingTrustId = "record", ExpectedTrustVersion = null }).HasValidBounds);
        Assert.False(new ConnectionTrustRolloverRequest(
            ConnectionTrustIpcContract.CurrentVersion,
            connectionId,
            ExpectedProfileVersion: 3,
            PreviousTrustId: "record",
            ExpectedPreviousTrustVersion: 1,
            NewSha256Fingerprint: new string('A', 64)) with
        {
            NewSha256Fingerprint = "SHA256:not-base64"
        } is { HasValidBounds: true });
    }

    [Fact]
    public void TrustSnapshotRejectsDuplicateIdsAndNonUtcHistory()
    {
        var record = new ConnectionTrustRecordDocument(
            "record-1",
            new string('A', 64),
            ConnectionTrustDecision.Trusted,
            DateTimeOffset.Parse("2026-08-02T12:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-02T12:00:00Z", CultureInfo.InvariantCulture),
            ExpiresUtc: null,
            PreviousFingerprint: null,
            Version: 1);
        var snapshot = new ConnectionTrustSnapshot(
            Guid.NewGuid(),
            ProfileVersion: 1,
            new ConnectionTrustTargetDocument(
                ConnectionTrustArtifactKind.SshHostKey,
                "sftp.example.test",
                22),
            [record]);

        Assert.True(snapshot.HasValidBounds);
        Assert.False((snapshot with { Records = [record, record] }).HasValidBounds);
        Assert.False((snapshot with
        {
            Records = [record with { LastSeenUtc = record.LastSeenUtc.ToOffset(TimeSpan.FromHours(2)) }]
        }).HasValidBounds);
    }

    [Fact]
    public void Transfer_enqueue_round_trips_bounded_version_and_entity_tag_identity()
    {
        var request = new TransferEnqueueRequest(
            TransferQueueIpcContract.CurrentVersion,
            Guid.NewGuid(),
            TransferQueueOperation.Move,
            new TransferQueueAddress(
                Guid.NewGuid(),
                "source-root",
                "folder/source.bin",
                "source-id",
                "source-version",
                "source-etag"),
            new TransferQueueAddress(
                Guid.NewGuid(),
                "destination-root",
                "archive/source.bin",
                "destination-id",
                "destination-version",
                "destination-etag"),
            ExpectedLength: 42,
            TransferQueueVerification.StrongHashRequired,
            Priority: 7,
            ExpectedDestinationVersionId: "destination-version",
            ExpectedDestinationEntityTag: "destination-etag");

        var restored = JsonSerializer.Deserialize<TransferEnqueueRequest>(
            JsonSerializer.Serialize(request));

        Assert.True(request.HasValidBounds);
        Assert.Equal(request, restored);
        Assert.Equal("source-etag", restored!.Source.EntityTag);
        Assert.Equal("destination-etag", restored.ExpectedDestinationEntityTag);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nvalue")]
    public void Transfer_addresses_reject_empty_whitespace_and_control_bearing_opaque_values(string value)
    {
        var address = new TransferQueueAddress(
            Guid.NewGuid(),
            "root",
            "item.bin",
            NativeItemId: value);

        Assert.False(address.HasValidBounds);
    }

    [Fact]
    public void Transfer_list_requires_distinct_bounded_states_and_page_size()
    {
        Assert.True(new TransferListRequest(
            TransferQueueIpcContract.CurrentVersion,
            [TransferQueueState.Pending, TransferQueueState.Retrying],
            TransferQueueIpcLimits.MaximumPageSize).HasValidBounds);
        Assert.False(new TransferListRequest(
            TransferQueueIpcContract.CurrentVersion,
            [TransferQueueState.Pending, TransferQueueState.Pending]).HasValidBounds);
        Assert.False(new TransferListRequest(
            TransferQueueIpcContract.CurrentVersion,
            [TransferQueueState.Pending],
            TransferQueueIpcLimits.MaximumPageSize + 1).HasValidBounds);
    }

    [Fact]
    public void Transfer_queue_responses_expose_summaries_not_provider_capabilities_or_secrets()
    {
        var responseTypes = new[]
        {
            typeof(TransferEnqueueResponse),
            typeof(TransferListResponse),
            typeof(TransferStatusResponse),
            typeof(TransferMutationResponse),
            typeof(TransferQueueSummary)
        };
        var forbiddenTerms = new[]
        {
            "secret",
            "password",
            "credential",
            "rootidentity",
            "nativeitem",
            "versionid",
            "entitytag",
            "resume",
            "lease",
            "fencing"
        };

        foreach (var property in responseTypes.SelectMany(static type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                forbiddenTerms,
                term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Sync_profile_draft_enforces_bounded_compatible_policy()
    {
        var draft = new SyncProfileDraftDocument(
            "Documents mirror",
            Guid.NewGuid(),
            "documents",
            Guid.NewGuid(),
            "backup/documents",
            SyncIpcDirection.LeftToRight,
            SyncIpcDeletionMode.Mirror,
            SyncIpcConflictPolicy.Block,
            MaximumDeletionCount: 100,
            MaximumDeletionPercentage: 10,
            Overwrite: true,
            TransferBufferSize: 65_536,
            Enabled: true)
        {
            AllowNonAtomicDestinationWrites = true
        };

        Assert.True(draft.HasValidBounds);
        var restored = JsonSerializer.Deserialize<SyncProfileDraftDocument>(JsonSerializer.Serialize(draft));
        Assert.NotNull(restored);
        Assert.True(restored.AllowNonAtomicDestinationWrites);
        Assert.Equal(draft, restored);
        Assert.False((draft with
        {
            Direction = SyncIpcDirection.TwoWay,
            DeletionMode = SyncIpcDeletionMode.Mirror
        }).HasValidBounds);
        Assert.False((draft with
        {
            LeftConnectionId = draft.RightConnectionId,
            LeftRoot = draft.RightRoot
        }).HasValidBounds);
        Assert.True((draft with
        {
            LeftConnectionId = draft.RightConnectionId,
            LeftRoot = "incoming",
            RightRoot = "archive"
        }).HasValidBounds);
        Assert.False((draft with
        {
            TransferBufferSize = SyncManagementIpcLimits.MaximumTransferBufferSize + 1
        }).HasValidBounds);
    }

    [Fact]
    public void Sync_approval_requires_exact_revision_bound_sha256()
    {
        var valid = new SyncApproveDispatchRequest(
            SyncManagementIpcContract.CurrentVersion,
            Guid.NewGuid(),
            ExpectedRevision: 3,
            ApprovalSha256: new string('a', 64));

        Assert.True(valid.HasValidBounds);
        Assert.False((valid with { ApprovalSha256 = "not-a-sha" }).HasValidBounds);
        Assert.False((valid with { ExpectedRevision = -1 }).HasValidBounds);
    }

    [Fact]
    public void Sync_responses_omit_provider_identity_and_execution_capability_fields()
    {
        var responseTypes = new[]
        {
            typeof(SyncProfileListResponse),
            typeof(SyncProfileGetResponse),
            typeof(SyncProfileMutationResponse),
            typeof(SyncPreviewGenerateResponse),
            typeof(SyncRunStatusResponse),
            typeof(SyncRunSummary),
            typeof(SyncPlanOverview),
            typeof(SyncPlanPageResponse),
            typeof(SyncPlanOperationSummary),
            typeof(SyncConflictPageResponse),
            typeof(SyncConflictSummary),
            typeof(SyncApproveDispatchResponse)
        };
        var forbiddenTerms = new[]
        {
            "secret",
            "password",
            "credential",
            "rootidentity",
            "nativeitem",
            "versionid",
            "entitytag",
            "lease",
            "fencing",
            "providercompleted"
        };

        foreach (var property in responseTypes.SelectMany(static type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                forbiddenTerms,
                term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Schedule_draft_is_bounded_and_preview_only()
    {
        var draft = new ScheduleDraftDocument(
            Guid.NewGuid(),
            "0 2 * * *",
            "UTC",
            MisfireGraceSeconds: 3_600,
            QueueOneWhileRunning: true,
            Enabled: true,
            ScheduleIpcExecutionMode.PreviewOnly);

        Assert.True(draft.HasValidBounds);
        Assert.False((draft with { CronExpression = new string('x', 129) }).HasValidBounds);
        Assert.False((draft with { MisfireGraceSeconds = 0 }).HasValidBounds);
        Assert.False((draft with { ExecutionMode = (ScheduleIpcExecutionMode)99 }).HasValidBounds);
    }

    [Fact]
    public void Schedule_responses_never_expose_ownership_or_fencing_evidence()
    {
        var responseTypes = new[]
        {
            typeof(ScheduleDocument),
            typeof(ScheduleListResponse),
            typeof(ScheduleGetResponse),
            typeof(ScheduleMutationResponse)
        };
        var forbidden = new[] { "lease", "fenc", "owner", "secret", "credential", "providercompleted" };

        foreach (var property in responseTypes.SelectMany(static type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                forbidden,
                term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Ssh_terminal_open_preferences_are_bounded()
    {
        var valid = new SshTerminalOpenRequest(
            SshTerminalIpcContract.CurrentVersion,
            Guid.NewGuid(),
            120,
            40,
            "screen-256color",
            "bash -l",
            KeepAliveSeconds: 90);

        Assert.True(valid.HasValidBounds);
        Assert.False((valid with { TerminalName = "xterm invalid" }).HasValidBounds);
        Assert.False((valid with { StartupCommand = "bash\n-l" }).HasValidBounds);
        Assert.False((valid with { StartupCommand = new string('x', 513) }).HasValidBounds);
        Assert.False((valid with { KeepAliveSeconds = -1 }).HasValidBounds);
        Assert.False((valid with { KeepAliveSeconds = 3_601 }).HasValidBounds);
    }
}
