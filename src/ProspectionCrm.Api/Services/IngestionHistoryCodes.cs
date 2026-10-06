namespace ProspectionCrm.Api.Services;

public static class IngestionHistoryCodes
{
    public const string Pending = "Pending";
    public const string CreatedNewOpportunity = "CreatedNewOpportunity";
    public const string MatchedExternalId = "MatchedExternalId";
    public const string MatchedSourceUrl = "MatchedSourceUrl";
    public const string MatchedTitleCompany = "MatchedTitleCompany";
    public const string MatchedConsistentIdentities = "MatchedConsistentIdentities";
    public const string DuplicateInBatch = "DuplicateInBatch";
    public const string AmbiguousIdentity = "AmbiguousIdentity";
    public const string MissingPersistentIdentity = "MissingPersistentIdentity";
    public const string ConcurrentIdentityChange = "ConcurrentIdentityChange";
    public const string PersistenceFailure = "PersistenceFailure";
    public const string RequestCancelled = "RequestCancelled";
    public const string RolledBackAfterFailure = "RolledBackAfterFailure";
    public const string NotProcessedAfterFailure = "NotProcessedAfterFailure";

    public static class Outcomes
    {
        public const string Pending = "pending";
        public const string Created = "created";
        public const string Updated = "updated";
        public const string Ignored = "ignored";
        public const string Rejected = "rejected";
        public const string RolledBack = "rolled-back";
        public const string NotProcessed = "not-processed";
        public const string Cancelled = "cancelled";
    }

    public static class Identities
    {
        public const string ExternalId = "external-id";
        public const string SourceUrl = "source-url";
        public const string TitleCompany = "title-company";
    }
}
