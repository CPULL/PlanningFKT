namespace minerva.planningfkt.models;

// Persisted "Fatto" tracking for the Fogli Firma lists (roster/da preparare/da
// chiudere) - CPU's call. Purely a UI checklist: never touches the real
// FoglioFirmaStatus/attendance data. Rows are created on page load for
// whatever the live computation currently contains (if not already present),
// and every row not dated today is deleted at the start of each load - so the
// checklist is always scoped to "today's list," never carrying over.
public class FoglioFirmaChecklistItem {
  public long Key { get; set; }

  // See FoglioFirmaChecklistType.
  public int Type { get; set; }

  public int PatientId { get; set; }

  public DateOnly MarkedDate { get; set; }

  public bool Fatto { get; set; }
}
