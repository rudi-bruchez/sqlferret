// src/SqlFerret.Core/Storage/PreparedPlanProfile.cs
using SqlFerret.Core.Plans;

namespace SqlFerret.Core.Storage;

/// <summary>
/// Un plan décodé, prêt à être inséré. Outcome porte `is_first` : seul WroteFirst le met à vrai.
/// </summary>
public record PreparedPlanProfile(PlanProfile Profile, PlanWriteOutcome Outcome);
