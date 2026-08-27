namespace minerva.planningfkt.models;

// "Importante!" tracking - marking an alert important moves it out of the normal
// card grid and into its own table (yellow border), until someone clicks
// "Gestito" to remove it for good. Keyed by the SAME 64-bit hash as
// AlertDismissal's Key, so an important alert is automatically excluded from the
// normal list too (no risk of seeing it twice, per CPU's call) and if the
// underlying condition resolves on its own, it just stops being computed and
// naturally disappears from the table as well - no stale row left behind.
public class AlertMarkedImportant {
  public long Key { get; set; }

  public DateTime MarkedAt { get; set; }

  // Which AlertType this row is - set on every row (CPU's call, "for future
  // use"), not just the stored-only types below.
  public AlertType Type { get; set; }

  // Only set for TherapistChangeNotification - that type has no "computed"
  // form at all, it only ever exists as a row here, created directly when a
  // reassignment changes both the therapist and the time together. Points at
  // the one TherapySlot that was reassigned (no history of the previous
  // therapist/time is kept anywhere - CPU's call).
  public int? SlotId { get; set; }
}
