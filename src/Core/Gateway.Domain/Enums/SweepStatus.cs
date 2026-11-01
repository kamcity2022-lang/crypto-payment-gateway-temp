namespace Gateway.Domain.Enums;

public enum SweepStatus
{
    PendingEvaluation = 1,
    FundingRequired = 2,
    ReadyToSign = 3,
    Broadcasting = 4,
    Completed = 5,
    Failed = 6
}