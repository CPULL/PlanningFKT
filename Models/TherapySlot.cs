namespace minerva.planningfkt.models;

public class TherapySlot {
  public int Id { get; set; }

  public int TherapyPartId { get; set; }

  public DateOnly Date { get; set; }

  public int TimeSlot { get; set; }

  public int? TherapistId { get; set; }

  public int Status { get; set; }

  // 0 = none. Positive N = N x 15 min of autonomous exercise AFTER this session;
  // negative N = N x 15 min BEFORE it. Only meaningful when this slot's
  // TherapyType.AllowsGinnasticaAttiva - same slot record, no separate status/
  // attendance/session-count effect (CPU's call).
  public int GinnasticaAttivaSlots { get; set; }

  // Extra 15-min units appended AFTER this session's normal TherapyType duration
  // (0 = none). Set automatically for Isocinetica: +1 on the therapy's first and
  // last session (only the last one for a renewal). Counted everywhere a slot's
  // length matters (grids, conflicts, capacity) via GetSlotSpan. A moved slot just
  // keeps its value - CPU's call, moves are not re-evaluated.
  public int ExtraSlots { get; set; }

  public int? RescheduledToId { get; set; }

  // A moved session that was Assente before the move (CPU): the row keeps
  // status Rescheduled like every moved session (so it never counts as placed,
  // occupies nothing and isn't in the absence alerts), and this flag makes it
  // show as a read-only "moved Assente" (arrow, semi-transparent) in Presenze,
  // Giorno, Settimana, Sposta sedute and Stato paziente.
  public bool WasAbsent { get; set; }

  // Set only by Presenze's status-cycling action for now (CPU's scope call) - other
  // places that touch Status/TherapistId don't populate these yet.
  public DateTime? ModDate { get; set; }

  public int? ModUser { get; set; }
}
