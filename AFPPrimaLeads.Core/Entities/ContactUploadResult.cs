namespace AFPPrimaLeads.Core.Entities
{
    public sealed record ContactUploadResult(string? ActionId, UploadFailureKind FailureKind = UploadFailureKind.Permanent)
    {
        public bool Success => ActionId is not null;
    }
}
