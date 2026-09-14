namespace minerva.planningfkt.models;

// TherapyType.Category
public static class TherapyCategory {
  public const int Reparto = 0;
  public const int Palestra = 1;

  public static string ToLabel(int category) {
    return category == Reparto ? "Reparto" : "Palestra";
  }
}

// TherapyType.Type
public static class TherapyExecutionType {
  public const int Light = 0;
  public const int Active = 1;

  public static string ToLabel(int type) {
    return type == Active ? "Con operatore" : "Senza operatore";
  }
}

// Patient.Sex
public static class PatientSex {
  public const int Male = 0;
  public const int Female = 1;
}

// Therapist.OperatingArea (bitfield)
// Bit 1 (1): Accettazione role (set) vs Terapista (unset)
// Bit 2 (2): Reparto (set) vs Palestra (unset) - Terapista only, meaningless for Accettazione
// Bit 3 (4): helps the other area too (either direction) - Terapista only
public static class TherapistOperatingArea {
  public const int Accettazione = 1;
  public const int Reparto = 2;
  public const int HelpsOtherArea = 4;

  public static string ToRoleLabel(int operatingArea) {
    if ((operatingArea & Accettazione) != 0) {
      return "Accettazione";
    }

    var isReparto = (operatingArea & Reparto) != 0;
    var helpsOther = (operatingArea & HelpsOtherArea) != 0;

    if (isReparto && helpsOther) {
      return "Reparto e aiuto a palestra";
    }

    if (isReparto) {
      return "Reparto";
    }

    if (helpsOther) {
      return "Palestra e aiuto a reparto";
    }

    return "Palestra";
  }
}

// Therapy.Status
public static class TherapyStatus {
  public const int ToBeScheduled = 0;
  public const int Scheduled = 1;
  public const int Completed = 2;
  public const int Cancelled = 3;

  public static string ToLabel(int status) {
    switch (status) {
      case ToBeScheduled: return "Da pianificare";
      case Scheduled: return "Pianificata";
      case Completed: return "Completata";
      case Cancelled: return "Annullata";
      default: return "Sconosciuto";
    }
  }
}

// TherapySlot.Status
public static class TherapySlotStatus {
  public const int ToBeDone = 0;
  public const int Done = 1;
  public const int PatientAbsent = 2;
  public const int Rescheduled = 3;
}

// Vacation.AMPM
public static class VacationPeriod {
  public const int AM = 0;
  public const int PM = 1;
}

// Therapy.BillingCategory - set at creation, never changes. Only Privata is ever
// treated differently anywhere in the app (Convenzione and Assicurazione behave
// identically - CPU's call) - kept as three named values rather than a bool
// because the office still needs to know which of the two non-private kinds a
// therapy is, even though the app itself doesn't branch on it.
public static class TherapyBillingCategory {
  public const int Convenzione = 0;
  public const int Privata = 1;
  public const int Assicurazione = 2;

  public static string ToLabel(int category) {
    switch (category) {
      case Convenzione: return "Terapia in Convenzione";
      case Privata: return "Terapia privata";
      case Assicurazione: return "Terapia con assicurazione/INAIL";
      default: return "Sconosciuto";
    }
  }
}

// Therapy.FoglioFirmaStatus - tracks the sign-in sheet(s) for a therapy as one
// group (CPU's call: they're always handled together, no need to count how many
// there are). Meaningless for Privata therapies - the app just never shows this
// row for those, rather than adding a "NotNeeded" value that would never apply.
public static class FoglioFirmaStatus {
  public const int ToBeCreated = 0;
  public const int InProgress = 1;
  public const int ToBeFinalized = 2;
  public const int Completed = 3;

  public static string ToLabel(int status) {
    switch (status) {
      case ToBeCreated: return "Foglio Firma da creare";
      case InProgress: return "In uso";
      case ToBeFinalized: return "Foglio Firma da chiudere";
      case Completed: return "Foglio Firma gestito";
      default: return "Sconosciuto";
    }
  }
}

// Every alert type Alerts/List can produce. Most are pure software - computed
// fresh each request, never stored. Stored only when an alert is marked
// "Importante!" (AlertMarkedImportant.Type) - kept mainly so we know which kind
// each stored row is, for future use. TherapistChangeNotification is the one
// exception that only ever exists as a stored row (see AlertMarkedImportant).
public enum AlertType {
  TherapyToBeScheduled,
  RepeatedNoShow2,
  RepeatedNoShow3,
  VacationConflict,
  TherapyRenewal,
  TherapistUnderScheduled,
  PastHolidayToUpdate,
  TherapistChangeNotification
}
