namespace minerva.planningfkt.models;

// Alerts are computed fresh on every request (nothing else about them is stored).
// This table only records that a specific computed alert (identified by a 64-bit
// hash of its type + the fields that define "the same issue") has been dismissed.
// Rows older than a month are pruned at the top of Alerts/List, so this table stays
// small - a dismissal naturally "expires" and the alert can resurface if the
// underlying issue is somehow still there a month later.
public class AlertDismissal {
  public long Key { get; set; }

  public DateTime DismissedAt { get; set; }
}
