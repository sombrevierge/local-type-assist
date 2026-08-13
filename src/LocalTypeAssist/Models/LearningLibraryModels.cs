namespace LocalTypeAssist.Models;

public enum LearningEventType
{
    TypedClean,
    TrainingObservation,
    AcceptedSuggestion,
    RejectedSuggestion,
    CorrectedAway,
    CorrectionTarget,
    DeletedToken,
    DismissedPopup
}

public sealed class LearningWordSignals
{
    public int ConfirmedCount { get; set; }
    public int TrainingObservationCount { get; set; }
    public int AcceptedSuggestionCount { get; set; }
    public int RejectedSuggestionCount { get; set; }
    public int CorrectedAwayCount { get; set; }
    public int CorrectionTargetCount { get; set; }
    public int DeletedCount { get; set; }
    public bool Trusted { get; set; }
    public bool Blocked { get; set; }
}

public sealed record LearningWordView(
    string Word,
    int TypedCount,
    int AcceptedCount,
    int TrainingCount,
    int CorpusCount,
    int CorrectedCount,
    int ConfirmedCount,
    int RejectedSuggestionCount,
    int CorrectedAwayCount,
    int CorrectionTargetCount,
    int DeletedCount,
    bool Trusted,
    bool Blocked,
    bool IsSeedWord,
    DateTime LastUsedUtc)
{
    public int PositiveSignals => TypedCount + AcceptedCount * 2 + TrainingCount * 3 +
                                  ConfirmedCount * 2 + CorrectionTargetCount * 3;

    public int NegativeSignals => CorrectedCount + CorrectedAwayCount * 3 + DeletedCount + RejectedSuggestionCount;

    public bool LikelyError =>
        !Trusted &&
        !IsSeedWord &&
        CorpusCount == 0 &&
        TrainingCount == 0 &&
        AcceptedCount == 0 &&
        (CorrectedAwayCount > 0 || CorrectedCount > 0) &&
        PositiveSignals <= Math.Max(3, NegativeSignals);

    // Broader, non-destructive review bucket. It intentionally includes old v6
    // one-off custom tokens that predate the v7 correction event log.
    public bool NeedsReview =>
        LikelyError ||
        (!Trusted &&
         !IsSeedWord &&
         CorpusCount == 0 &&
         TrainingCount == 0 &&
         AcceptedCount == 0 &&
         CorrectionTargetCount == 0 &&
         TypedCount <= 1 &&
         ConfirmedCount <= 1);
}

public sealed record LearningCorrectionView(
    string Original,
    string Corrected,
    int Count,
    DateTime LastSeenUtc);

public sealed record MlModelStatus(
    bool Available,
    int SampleCount,
    int PositiveSamples,
    DateTime? TrainedAtUtc,
    string Message);
