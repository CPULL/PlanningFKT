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
}
