using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class PacchettoItemRequest {
    public int TherapyTypeId { get; set; }
    public int SessionCount { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
  }

  public class PacchettoSaveRequest {
    public string? Name { get; set; }
    public int? PatientId { get; set; }
    public string? PersonName { get; set; }
    public string? Approvatore { get; set; }
    public decimal TotalPrice { get; set; }
    public DateTime? CreatedAt { get; set; }
    public List<PacchettoItemRequest> Items { get; set; } = new();
  }

  public class PacchettoRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  private static bool IsValidPacchettoItemsList(List<PacchettoItemRequest>? items) {
    return items != null && items.Count > 0 && items.All(i => i.SessionCount >= 1 && i.UnitPrice >= 0
      && i.DiscountPercent >= 0 && i.DiscountPercent <= 100);
  }

  // Patient-linked packets use the patient's own name; free-text-person packets use
  // that typed name; truly Generico packets (both null) fall back to "Pacchetto #id".
  private string GetPacchettoConfirmName(TherapyPacket packet) {
    if (packet.PatientId.HasValue) {
      var patient = _db.Patients.Find(packet.PatientId.Value);
      return patient?.Name ?? "?";
    }
    if (!string.IsNullOrEmpty(packet.PersonName)) {
      return packet.PersonName;
    }
    return "Pacchetto #" + packet.Id;
  }

  public class PacchettoPriceResult {
    public decimal TotalNormal { get; set; }
    public decimal TotalDiscounted { get; set; }
    public int QtyCapped { get; set; }
    public decimal ScontoCalcolato { get; set; } // percent, e.g. 9.4
    public decimal PacketCost { get; set; }
  }

  // Corrected per CPU's clarification (caught before production): the extra
  // volume discount is based on how many DISTINCT therapies (numT = line
  // count) are bundled together, NOT total sessions. A packet with a single
  // therapy gets ONLY that line's own discount - no extra layer at all,
  // regardless of how many sessions it has. The extra layer only starts
  // applying at numT=2, ramping linearly up to PacchettoScontoMassimo at
  // numT=8 (capped there for anything higher).
  // - totalNormal = Sum(price * sessions), rounded (display only)
  // - totalDiscounted = Sum(price * sessions * (1 - rowDiscount)), rounded
  //   (display only) - each line's own discount always applies regardless of numT
  // - numT (QtyCapped, kept as the field name for now) = min(8, distinct line count)
  // - scontoCalcolato = 0 when numT==1; otherwise linear interpolation of
  //   scontoMin (at numT=2) .. scontoMax (at numT=8)
  // - packetCost = round-to-5(totalDiscountedRAW * (1 - scontoCalcolato)) -
  //   computed from the UNROUNDED discounted total, not the rounded display value
  private PacchettoPriceResult ComputePacchettoPrice(List<PacchettoItemRequest> items) {
    if (items.Count == 0) {
      return new PacchettoPriceResult { TotalNormal = 0, TotalDiscounted = 0, QtyCapped = 0, ScontoCalcolato = 0, PacketCost = 0 };
    }

    var totalNormalRaw = items.Sum(i => i.UnitPrice * i.SessionCount);
    var totalDiscountedRaw = items.Sum(i => i.UnitPrice * i.SessionCount * (1 - i.DiscountPercent / 100m));
    var numT = Math.Min(8, items.Count);

    decimal scontoCalcolato;
    if (numT <= 1) {
      // A single therapy never gets the extra layer, no matter the session count.
      scontoCalcolato = 0m;
    } else {
      var scontoMinSetting = _settingsCache.Get("PacchettoScontoMinimo", 5);
      var scontoMaxSetting = _settingsCache.Get("PacchettoScontoMassimo", 25);
      var scontoMin = (scontoMinSetting <= 0 ? 5m : scontoMinSetting) / 100m;
      var scontoMax = (scontoMaxSetting <= 0 ? 25m : scontoMaxSetting) / 100m;

      // numT=2 -> scontoMin, numT=8 -> scontoMax, linear in between.
      scontoCalcolato = scontoMin * (8 - numT) / 6m + scontoMax * (numT - 2) / 6m;
    }

    var packetCost = Math.Round(totalDiscountedRaw * (1 - scontoCalcolato) / 5m, MidpointRounding.AwayFromZero) * 5m;

    return new PacchettoPriceResult {
      TotalNormal = Math.Round(totalNormalRaw, 2),
      TotalDiscounted = Math.Round(totalDiscountedRaw, 2),
      QtyCapped = numT,
      ScontoCalcolato = Math.Round(scontoCalcolato * 100m, 2),
      PacketCost = packetCost
    };
  }

  [HttpGet("/Pacchetti/List")]
  public IActionResult PacchettiList(bool includeAudit = false) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var patientNames = _db.Patients.ToDictionary(p => p.Id, p => p.Name);
    var typeNames = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Name);
    var modifierNames = includeAudit
      ? _db.Therapists.ToDictionary(t => t.Id, t => t.Name)
      : new Dictionary<int, string>();

    var packets = _db.TherapyPackets.OrderByDescending(p => p.CreatedAt).ToList();
    var itemsByPacket = _db.TherapyPacketItems.ToList().GroupBy(i => i.TherapyPacketId).ToDictionary(g => g.Key, g => g.ToList());

    var rows = packets.Select(p => {
      var items = itemsByPacket.ContainsKey(p.Id) ? itemsByPacket[p.Id] : new List<TherapyPacketItem>();
      var itemsSummary = string.Join(", ", items.Select(i =>
        i.SessionCount + " × " + (typeNames.ContainsKey(i.TherapyTypeId) ? typeNames[i.TherapyTypeId] : "?")));
      var nominalTotal = items.Sum(i => i.UnitPrice * i.SessionCount);

      return new {
        Id = (int?)p.Id,
        isTherapyType = false,
        p.Name,
        patientName = (string?)(p.PatientId.HasValue
          ? (patientNames.ContainsKey(p.PatientId.Value) ? patientNames[p.PatientId.Value] : "?")
          : (p.PersonName ?? "Generico")),
        p.Approvatore,
        nominalPrice = (decimal?)Math.Round(nominalTotal, 2),
        p.TotalPrice,
        CreatedAt = (DateTime?)p.CreatedAt,
        itemsSummary,
        modDate = includeAudit ? p.ModDate : (DateTime?)null,
        modifier = includeAudit && modifierNames.ContainsKey(p.ModUser) ? modifierNames[p.ModUser] : null
      };
    }).ToList();

    // Every TherapyType with a Tariffario price mixed in among the real
    // packets - purely informational (CPU's call), so a person can also see
    // "just this one therapy" priced the same way, without creating anything.
    // Always assumed at 10 sessions - the packet formula's own volume-discount
    // cap, so this is really just ComputePacchettoPrice at its top tier.
    var configs = _db.TherapyPacketConfigs.ToList();
    var typeTypeRows = configs.Select(c => {
      var typeName = typeNames.ContainsKey(c.TherapyTypeId) ? typeNames[c.TherapyTypeId] : "?";
      var priceResult = ComputePacchettoPrice(new List<PacchettoItemRequest> {
        new PacchettoItemRequest {
          TherapyTypeId = c.TherapyTypeId,
          SessionCount = 10,
          UnitPrice = c.UnitPrice,
          DiscountPercent = c.DefaultDiscountPercent
        }
      });

      return new {
        Id = (int?)null,
        isTherapyType = true,
        Name = (string?)typeName,
        patientName = (string?)null,
        Approvatore = (string?)null,
        nominalPrice = (decimal?)priceResult.TotalNormal,
        TotalPrice = priceResult.PacketCost,
        CreatedAt = (DateTime?)null,
        itemsSummary = "10 × " + typeName,
        modDate = (DateTime?)null,
        modifier = (string?)null
      };
    });

    return Ok(rows.Concat(typeTypeRows));
  }

  [HttpGet("/Pacchetti/Get/{id}")]
  public IActionResult PacchettiGet(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var packet = _db.TherapyPackets.Find(id);

    if (packet == null) {
      return NotFound();
    }

    var patient = packet.PatientId.HasValue ? _db.Patients.Find(packet.PatientId.Value) : null;
    var items = _db.TherapyPacketItems.Where(i => i.TherapyPacketId == id).ToList();
    var typeNames = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Name);

    return Ok(new {
      packet.Id,
      packet.Name,
      packet.PatientId,
      patientName = patient?.Name,
      packet.PersonName,
      packet.Approvatore,
      packet.TotalPrice,
      packet.CreatedAt,
      confirmName = GetPacchettoConfirmName(packet),
      items = items.Select(i => new {
        i.TherapyTypeId,
        therapyTypeName = typeNames.ContainsKey(i.TherapyTypeId) ? typeNames[i.TherapyTypeId] : "?",
        i.SessionCount,
        i.UnitPrice,
        i.DiscountPercent
      })
    });
  }

  [HttpPost("/Pacchetti/Create")]
  public IActionResult PacchettiCreate([FromBody] PacchettoSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    if (request.PatientId.HasValue && _db.Patients.Find(request.PatientId.Value) == null) {
      return NotFound();
    }

    if (!IsValidPacchettoItemsList(request.Items)) {
      return BadRequest(new { message = "È necessaria almeno una terapia valida, con almeno una seduta ciascuna." });
    }

    var packet = new TherapyPacket {
      Name = request.Name,
      PatientId = request.PatientId,
      PersonName = request.PersonName,
      Approvatore = request.Approvatore,
      TotalPrice = request.TotalPrice,
      CreatedAt = request.CreatedAt ?? DateTime.Now,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    _db.TherapyPackets.Add(packet);
    _db.SaveChanges();

    foreach (var item in request.Items) {
      _db.TherapyPacketItems.Add(new TherapyPacketItem {
        TherapyPacketId = packet.Id,
        TherapyTypeId = item.TherapyTypeId,
        SessionCount = item.SessionCount,
        UnitPrice = item.UnitPrice,
        DiscountPercent = item.DiscountPercent
      });
    }

    _db.SaveChanges();

    return Ok(new { packet.Id });
  }

  [HttpPut("/Pacchetti/Update/{id}")]
  public IActionResult PacchettiUpdate(int id, [FromBody] PacchettoSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var packet = _db.TherapyPackets.Find(id);

    if (packet == null) {
      return NotFound();
    }

    if (request.PatientId.HasValue && _db.Patients.Find(request.PatientId.Value) == null) {
      return NotFound();
    }

    if (!IsValidPacchettoItemsList(request.Items)) {
      return BadRequest(new { message = "È necessaria almeno una terapia valida, con almeno una seduta ciascuna." });
    }

    packet.Name = request.Name;
    packet.PatientId = request.PatientId;
    packet.PersonName = request.PersonName;
    packet.Approvatore = request.Approvatore;
    packet.TotalPrice = request.TotalPrice;
    if (request.CreatedAt.HasValue) {
      packet.CreatedAt = request.CreatedAt.Value;
    }
    packet.ModDate = DateTime.Now;
    packet.ModUser = currentUserId.Value;

    var existingItems = _db.TherapyPacketItems.Where(i => i.TherapyPacketId == id);
    _db.TherapyPacketItems.RemoveRange(existingItems);

    foreach (var item in request.Items) {
      _db.TherapyPacketItems.Add(new TherapyPacketItem {
        TherapyPacketId = id,
        TherapyTypeId = item.TherapyTypeId,
        SessionCount = item.SessionCount,
        UnitPrice = item.UnitPrice,
        DiscountPercent = item.DiscountPercent
      });
    }

    _db.SaveChanges();

    return Ok();
  }

  [HttpPost("/Pacchetti/Remove/{id}")]
  public IActionResult PacchettiRemove(int id, [FromBody] PacchettoRemoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var packet = _db.TherapyPackets.Find(id);

    if (packet == null) {
      return NotFound();
    }

    var expectedName = GetPacchettoConfirmName(packet);

    if (!string.Equals(request.ConfirmName, expectedName, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest();
    }

    var items = _db.TherapyPacketItems.Where(i => i.TherapyPacketId == id);
    _db.TherapyPacketItems.RemoveRange(items);
    _db.TherapyPackets.Remove(packet);
    _db.SaveChanges();

    return Ok();
  }

  // Recomputes the proposal server-side from the submitted rows, so the client never
  // has to trust its own math for the confirm-popup breakdown.
  [HttpPost("/Pacchetti/ProposeFinalPrice")]
  public IActionResult PacchettiProposeFinalPrice([FromBody] List<PacchettoItemRequest> items) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (!IsValidPacchettoItemsList(items)) {
      return Ok(new PacchettoPriceResult { TotalNormal = 0, TotalDiscounted = 0, QtyCapped = 0, ScontoCalcolato = 0, PacketCost = 0 });
    }

    return Ok(ComputePacchettoPrice(items!));
  }

  // --- Rate card (TherapyPacketConfig) - one row per TherapyType, used only to
  // prefill a new item row's UnitPrice/DiscountPercent. TherapyType itself is
  // never touched here. --------------------------------------------------------

  public class PacchettoConfigSaveRequest {
    public int TherapyTypeId { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DefaultDiscountPercent { get; set; }
  }

  [HttpGet("/Pacchetti/Config/List")]
  public IActionResult PacchettiConfigList(bool includeAudit = false) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var typeNames = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Name);
    var modifierNames = includeAudit
      ? _db.Therapists.ToDictionary(t => t.Id, t => t.Name)
      : new Dictionary<int, string>();

    var rows = _db.TherapyPacketConfigs.ToList()
      .Select(c => new {
        c.Id,
        c.TherapyTypeId,
        therapyTypeName = typeNames.ContainsKey(c.TherapyTypeId) ? typeNames[c.TherapyTypeId] : "?",
        c.UnitPrice,
        c.DefaultDiscountPercent,
        modDate = includeAudit ? c.ModDate : (DateTime?)null,
        modifier = includeAudit && c.ModUser.HasValue && modifierNames.ContainsKey(c.ModUser.Value) ? modifierNames[c.ModUser.Value] : null
      })
      .OrderBy(c => c.therapyTypeName);

    return Ok(rows);
  }

  [HttpPost("/Pacchetti/Config/Create")]
  public IActionResult PacchettiConfigCreate([FromBody] PacchettoConfigSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (_db.TherapyTypes.Find(request.TherapyTypeId) == null) {
      return NotFound();
    }

    if (_db.TherapyPacketConfigs.Any(c => c.TherapyTypeId == request.TherapyTypeId)) {
      return BadRequest(new { message = "Esiste già una voce di tariffario per questa terapia." });
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var config = new TherapyPacketConfig {
      TherapyTypeId = request.TherapyTypeId,
      UnitPrice = request.UnitPrice,
      DefaultDiscountPercent = request.DefaultDiscountPercent,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    _db.TherapyPacketConfigs.Add(config);
    _db.SaveChanges();

    return Ok(new { config.Id });
  }

  [HttpPut("/Pacchetti/Config/Update/{id}")]
  public IActionResult PacchettiConfigUpdate(int id, [FromBody] PacchettoConfigSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var config = _db.TherapyPacketConfigs.Find(id);

    if (config == null) {
      return NotFound();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    if (_db.TherapyTypes.Find(request.TherapyTypeId) == null) {
      return NotFound();
    }

    if (_db.TherapyPacketConfigs.Any(c => c.TherapyTypeId == request.TherapyTypeId && c.Id != id)) {
      return BadRequest(new { message = "Esiste già una voce di tariffario per questa terapia." });
    }

    config.TherapyTypeId = request.TherapyTypeId;
    config.UnitPrice = request.UnitPrice;
    config.DefaultDiscountPercent = request.DefaultDiscountPercent;
    config.ModDate = DateTime.Now;
    config.ModUser = currentUserId.Value;
    _db.SaveChanges();

    return Ok();
  }

  [HttpPost("/Pacchetti/Config/Remove/{id}")]
  public IActionResult PacchettiConfigRemove(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var config = _db.TherapyPacketConfigs.Find(id);

    if (config == null) {
      return NotFound();
    }

    _db.TherapyPacketConfigs.Remove(config);
    _db.SaveChanges();

    return Ok();
  }
}
