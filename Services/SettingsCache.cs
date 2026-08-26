using Microsoft.Extensions.DependencyInjection;
using minerva.planningfkt.models;

namespace minerva.planningfkt.services;

// App-lifetime singleton cache of the Settings table (Key -> Value). Settings change
// only through an explicit admin action (AppController.Settings.cs's SettingsUpdate)
// and are read on nearly every request, so unlike the per-request caches in
// AppController (thrown away at the end of each request), this one is meant to
// survive across requests - it's loaded once at startup and reloaded once after every
// write, so it's never more than one write behind.
//
// Reads swap in an already-built dictionary reference (no locking): a reload builds a
// brand new Dictionary and only then publishes it, so a request reading mid-reload
// either sees the old, still-consistent snapshot or the new one, never a half-built one.
public class SettingsCache {
  private readonly IServiceScopeFactory _scopeFactory;
  private volatile Dictionary<string, int> _values;

  public SettingsCache(IServiceScopeFactory scopeFactory) {
    _scopeFactory = scopeFactory;
    _values = new Dictionary<string, int>();
    Reload();
  }

  public void Reload() {
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    _values = db.Settings.ToDictionary(s => s.Key, s => s.Value);
  }

  public int Get(string key, int defaultValue) {
    return _values.TryGetValue(key, out var value) ? value : defaultValue;
  }
}
