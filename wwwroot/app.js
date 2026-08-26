$(function() {
  var showRemoved = false;
  var showAudit = false;
  var showCapacityDebug = false;
  var currentView = null;
  var schedulingTherapists = null;
  var giornoCurrentDate = null;
  var giornoMode = null; // 'therapist' | 'reparto' | null
  var giornoSelectedTherapistId = null;
  var giornoSelectedRepartoSex = null; // 0 (Uomini) | 1 (Donne) | null
  var presenzeCurrentDate = null;
  var presenzeMode = null; // 'therapist' | 'reparto' | null
  var presenzeSelectedTherapistId = null;
  var presenzeSelectedRepartoSex = null; // 0 (Uomini) | 1 (Donne) | null
  var meseCurrentYear = null;
  var meseCurrentMonth = null; // 1-12
  var meseSelectedTherapistId = null; // null = global (all therapists + Reparto)
  var settimanaCurrentWeekStart = null;
  var settimanaMode = null; // 'therapist' | 'reparto' | null
  var settimanaSelectedTherapistId = null;
  var settimanaSelectedRepartoSex = null; // 0 (Uomini) | 1 (Donne) | null
  var printSourceView = null; // 'giorno' | 'settimana' - which view Stampa/Chiudi return to
  var suppressHashChange = false;
  var ripianificaDraft = null; // { originalSlotId, therapyPartId, therapistId, therapistName, patientName, therapyTypeName, therapyTypeColor, durationSlots, date, timeSlot } | null
  var ripianificaReturnView = null; // set when Ripianifica was triggered from somewhere other than Settimana (e.g. Gestisci Assenza) - where Confirma/Annulla should navigate back to instead of staying on Settimana

  // Rapid day/week navigation used to leave the page showing whichever request
  // happened to resolve LAST, not whichever the user actually navigated to
  // (CPU: "scrolling fast makes the page impossible to handle"). Two layers of
  // defense: abort the previous in-flight request outright when a new one starts,
  // AND have every response echo back the request it answered, so even a response
  // that slips through before its abort takes effect gets discarded if it no
  // longer matches the current live selection.
  var giornoActiveRequest = null;
  var settimanaActiveRequest = null;
  var presenzeActiveRequest = null;

  function abortIfActive(request) {
    if (request && request.readyState !== 4) {
      request.abort();
    }
  }

  // Shared quick date-picker for Giorno/Settimana/Presenze - click the date label
  // (cursor:pointer, no visible button per CPU's call) to jump straight to a day
  // instead of stepping one at a time. Weekends are never shown - there's nothing
  // to schedule on them. Settimana passes an onPick that jumps to the containing
  // week instead of the exact day.
  function showQuickDatePicker(initialDate, onPick) {
    var pickerYear = initialDate.getFullYear();
    var pickerMonth = initialDate.getMonth(); // 0-based

    var $body = $('<div></div>');

    function render() {
      $body.empty();

      var $nav = $('<div class="quick-date-picker-nav"></div>');
      var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
      var $label = $('<span></span>').text(monthNamesFull[pickerMonth] + ' ' + pickerYear);
      var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');

      $prev.on('click', function() {
        pickerMonth -= 1;
        if (pickerMonth < 0) {
          pickerMonth = 11;
          pickerYear -= 1;
        }
        render();
      });
      $next.on('click', function() {
        pickerMonth += 1;
        if (pickerMonth > 11) {
          pickerMonth = 0;
          pickerYear += 1;
        }
        render();
      });

      $nav.append($prev).append($label).append($next);
      $body.append($nav);

      var $table = $('<table class="mese-table quick-date-picker-table"></table>');
      $table.append('<thead><tr><th>Lun</th><th>Mar</th><th>Mer</th><th>Gio</th><th>Ven</th></tr></thead>');
      var $tbody = $('<tbody></tbody>');

      var firstOfMonth = new Date(pickerYear, pickerMonth, 1);
      var lastOfMonth = new Date(pickerYear, pickerMonth + 1, 0);
      var gridStart = getMondayOfWeek(firstOfMonth);
      var gridEnd = getMondayOfWeek(lastOfMonth);
      gridEnd.setDate(gridEnd.getDate() + 4);

      var cursor = new Date(gridStart);
      while (cursor <= gridEnd) {
        var $row = $('<tr></tr>');

        for (var i = 0; i < 5; i++) {
          var inMonth = cursor.getMonth() === pickerMonth;
          var $cell = $('<td class="mese-cell quick-date-picker-cell"></td>');

          if (inMonth) {
            $cell.text(cursor.getDate());
            (function(pickedDate) {
              $cell.on('click', function() {
                $('#modal-overlay').hide();
                onPick(pickedDate);
              });
            })(new Date(cursor));
          } else {
            $cell.addClass('mese-cell-outside');
          }

          $row.append($cell);
          cursor.setDate(cursor.getDate() + 1);
        }

        cursor.setDate(cursor.getDate() + 2); // skip Sat/Sun
        $tbody.append($row);
      }

      $table.append($tbody);
      $body.append($table);
    }

    render();

    window.showModal('', [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
    $('#modal-body').empty().append($body);
  }

  var sectionLabels = {
    presenze: 'Presenze',
    giorno: 'Giorno',
    settimana: 'Settimana',
    mese: 'Mese',
    pazienti: 'Pazienti',
    avvisi: 'Avvisi',
    utenti: 'Utenti',
    assenze: 'Vacanze',
    terapie: 'Terapie',
    impostazioni: 'Impostazioni',
    pacchetti: 'Pacchetti',
    gestisciAssenza: 'Gestisci assenza'
  };

  // Impostazioni: user-readable labels for known Setting keys, falling back to the raw
  // key for anything not yet mapped. isTime flags the keys that store a 15-min slot index
  // (same convention as TherapySlot/TherapistAvailability) and should show/edit as HH:MM
  // via the same time-picker popup used on the Utenti availability grid.
  var settingLabels = {
    AvailabilityStart: { label: 'Ora inizio attività del centro', isTime: true },
    AvailabilityEnd: { label: 'Ora fine attività del centro', isTime: true },
    RepartoTherapyStartingTime: { label: 'Tempo di avvio terapia in Reparto (minuti da specialista)', isTime: false },
    PalestraCoveringRepartoStartingTime: { label: 'Tempo di avvio terapia in Reparto (minuti da ausiliario)', isTime: false },
    RepartoCapacityWarningThreshold: { label: 'Tasso di occupazione del reparto per saturazione (percentuale, default 75)', isTime: false },
    PacchettoScontoMinimo: { label: 'Sconto minimo per i pacchetti (percentuale, default 5)', isTime: false },
    PacchettoScontoMassimo: { label: 'Sconto massimo per i pacchetti (percentuale, default 25)', isTime: false }
  };

  // Weekday convention matches System.DayOfWeek on the backend: Monday = 1 ... Friday = 5.
  var weekDays = [
    { value: 1, label: 'Lunedì' },
    { value: 2, label: 'Martedì' },
    { value: 3, label: 'Mercoledì' },
    { value: 4, label: 'Giovedì' },
    { value: 5, label: 'Venerdì' }
  ];

  // Page title (left side of the topbar) is always shown; any contextual action
  // buttons for the current view sit alongside it. Call this any time
  // topbar-actions' content changes.
  function setTopbarActions(content) {
    var hasButtons = !!content;
    var title = hasButtons ? (sectionLabels[currentView] || '') : ('Planning FKT - ' + (sectionLabels[currentView] || ''));

    $('#topbar-fallback-title').text(title);
    $('#topbar-fallback').show();

    $('#topbar-actions').empty();

    if (content) {
      $('#topbar-actions').append(content);
    }
  }

  // Generic dirty-tracking: Save stays disabled until something in $container actually changes.
  // Works for dynamically added/removed inputs too (e.g. availability rows), since the snapshot
  // is recomputed from whatever inputs currently exist, not a fixed list captured up front.
  // Wraps a (full-width, block-level) input so its required-marker asterisk can be
  // positioned at the input's own top-right corner, instead of falling below it as
  // a plain next-sibling would (since .form-box inputs are width:100%/block).
  function markRequired($input) {
    $input.wrap('<div class="field-wrap"></div>');
    $input.after('<span class="required-marker"></span>');
  }

  function trackDirty($container, $saveBtn, $cancelBtn) {
    function snapshot() {
      var fields = $container.find('input, select').map(function() {
        var $el = $(this);

        if ($el.attr('type') === 'checkbox') {
          return $el.prop('checked') ? '1' : '0';
        }

        return $el.val();
      }).get().join('|');

      // Time-box buttons (availability picker) aren't input/select elements,
      // so their value has to be captured separately.
      var timeBoxes = $container.find('.availability-time-box').map(function() {
        return $(this).data('slot');
      }).get().join('|');

      return fields + '||' + timeBoxes;
    }

    var baseline = snapshot();

    function refresh() {
      var isDirty = snapshot() !== baseline;
      $saveBtn.prop('disabled', !isDirty);

      if ($cancelBtn) {
        $cancelBtn.text(isDirty ? 'Cancella' : 'Chiudi');
      }
    }

    $container.on('input change', 'input, select', refresh);
    refresh();

    return {
      refresh: refresh,
      resetBaseline: function() {
        baseline = snapshot();
        refresh();
      }
    };
  }

  // --- Shared: edge-of-content overlay bars for drag-hover week navigation ---
  // Used by both Pianificazione flows (Palestra + Reparto). Two fixed, pointer-events:none
  // gradient bars sit at the left/right edges of #content, repositioned dynamically off
  // the grid table's own rect. A document-level dragover listener compares the drag
  // pointer's coordinates against each bar's current rect (can't rely on e.target/closest
  // since pointer-events:none makes the bars invisible to hit-testing) - entering a bar's
  // region shows it and starts a 2.5s hold; leaving cancels it; completing it navigates
  // the week. Since the bars don't receive pointer events, a drop in that screen region
  // passes straight through to whatever real grid cell is underneath.
  var edgeOverlayLeft = null;
  var edgeOverlayRight = null;
  var edgeHoverSide = null;
  var edgeHoverTimer = null;

  function removeEdgeOverlays() {
    $('.pianificazione-edge-overlay').remove();
    edgeOverlayLeft = null;
    edgeOverlayRight = null;
    edgeHoverSide = null;

    if (edgeHoverTimer) {
      clearTimeout(edgeHoverTimer);
      edgeHoverTimer = null;
    }

    $(document).off('dragover.pianEdgeOverlay dragend.pianEdgeOverlay drop.pianEdgeOverlay');
  }

  function setupEdgeOverlays(onNavigate) {
    removeEdgeOverlays();

    edgeOverlayLeft = $('<div class="pianificazione-edge-overlay pianificazione-edge-overlay-left"></div>');
    edgeOverlayRight = $('<div class="pianificazione-edge-overlay pianificazione-edge-overlay-right"></div>');
    $('body').append(edgeOverlayLeft).append(edgeOverlayRight);

    function deactivate() {
      if (edgeHoverTimer) {
        clearTimeout(edgeHoverTimer);
        edgeHoverTimer = null;
      }
      if (edgeOverlayLeft) {
        edgeOverlayLeft.removeClass('pianificazione-edge-overlay-visible');
      }
      if (edgeOverlayRight) {
        edgeOverlayRight.removeClass('pianificazione-edge-overlay-visible');
      }
      edgeHoverSide = null;
    }

    function activate(side) {
      if (edgeHoverSide === side) {
        return; // already counting down on this side
      }

      deactivate();
      edgeHoverSide = side;
      (side === 'left' ? edgeOverlayLeft : edgeOverlayRight).addClass('pianificazione-edge-overlay-visible');

      edgeHoverTimer = setTimeout(function() {
        edgeHoverTimer = null;
        deactivate();
        onNavigate(side);
      }, 2000);
    }

    $(document).on('dragover.pianEdgeOverlay', function(e) {
      if (!edgeOverlayLeft || !edgeOverlayRight) {
        return;
      }

      var x = e.originalEvent.clientX;
      var y = e.originalEvent.clientY;
      var leftRect = edgeOverlayLeft[0].getBoundingClientRect();
      var rightRect = edgeOverlayRight[0].getBoundingClientRect();

      var overLeft = x >= leftRect.left && x <= leftRect.right && y >= leftRect.top && y <= leftRect.bottom;
      var overRight = x >= rightRect.left && x <= rightRect.right && y >= rightRect.top && y <= rightRect.bottom;

      if (overLeft) {
        activate('left');
      } else if (overRight) {
        activate('right');
      } else {
        deactivate();
      }
    });

    $(document).on('dragend.pianEdgeOverlay drop.pianEdgeOverlay', function() {
      deactivate();
    });
  }

  function positionEdgeOverlays($table) {
    if (!edgeOverlayLeft || !edgeOverlayRight || !$table || $table.length === 0) {
      return;
    }

    var contentEl = document.getElementById('content');
    if (!contentEl) {
      return;
    }

    var contentRect = contentEl.getBoundingClientRect();
    var tableRect = $table[0].getBoundingClientRect();

    edgeOverlayLeft.css({
      top: contentRect.top + 'px',
      height: contentRect.height + 'px',
      left: tableRect.left + 'px'
    });

    var rightWidth = edgeOverlayRight.outerWidth();
    edgeOverlayRight.css({
      top: contentRect.top + 'px',
      height: contentRect.height + 'px',
      left: (tableRect.right - rightWidth) + 'px'
    });
  }

  // Fixed 15-minute row height for the Pianificazione grids - lets a session's overlay
  // div span multiple rows via a computed pixel height (rows * PIANIFICAZIONE_ROW_HEIGHT).
  var PIANIFICAZIONE_ROW_HEIGHT = 24;

  // Sweep-line grouping: sorts by start, merges into a group whenever the next item's
  // start is before the current group's running max-end (transitively connects chains
  // of overlapping ranges). Every item in a group gets an equal width slice and a
  // left-offset based on its position within the group - not full interval-graph
  // coloring/minimal-lane packing, just an equal-split approximation, per spec.
  function computeOverlapGroups(items) {
    var sorted = items.slice().sort(function(a, b) { return a.startSlot - b.startSlot; });
    var groups = [];
    var currentGroup = [];
    var currentEnd = -1;

    sorted.forEach(function(item) {
      if (currentGroup.length > 0 && item.startSlot < currentEnd) {
        currentGroup.push(item);
        currentEnd = Math.max(currentEnd, item.endSlot);
      } else {
        if (currentGroup.length > 0) {
          groups.push(currentGroup);
        }
        currentGroup = [item];
        currentEnd = item.endSlot;
      }
    });

    if (currentGroup.length > 0) {
      groups.push(currentGroup);
    }

    var result = {};
    groups.forEach(function(group) {
      group.forEach(function(item, idx) {
        result[item.key] = { groupSize: group.length, indexInGroup: idx };
      });
    });

    return result;
  }

  function slotToTime(slot) {
    var totalMinutes = slot * 15;
    var h = Math.floor(totalMinutes / 60);
    var m = totalMinutes % 60;
    return (h < 10 ? '0' : '') + h + ':' + (m < 10 ? '0' : '') + m;
  }

  function timeToSlot(timeStr) {
    var parts = timeStr.split(':');
    var h = parseInt(parts[0], 10);
    var m = parseInt(parts[1], 10);
    return Math.round((h * 60 + m) / 15);
  }

  // Date helpers for Giorno/Settimana. JS Date.getDay() (0=Sunday...6=Saturday) matches
  // the backend's DayOfWeek convention (System.DayOfWeek, Monday=1...Friday=5) directly.
  var dayFullNames = ['Domenica', 'Lunedì', 'Martedì', 'Mercoledì', 'Giovedì', 'Venerdì', 'Sabato'];
  var monthNamesFull = [
    'Gennaio', 'Febbraio', 'Marzo', 'Aprile', 'Maggio', 'Giugno',
    'Luglio', 'Agosto', 'Settembre', 'Ottobre', 'Novembre', 'Dicembre'
  ];

  function formatDateDDMM(date) {
    var dd = (date.getDate() < 10 ? '0' : '') + date.getDate();
    var mm = (date.getMonth() + 1 < 10 ? '0' : '') + (date.getMonth() + 1);
    return dd + '/' + mm;
  }

  function formatDateISO(date) {
    var yyyy = date.getFullYear();
    var mm = (date.getMonth() + 1 < 10 ? '0' : '') + (date.getMonth() + 1);
    var dd = (date.getDate() < 10 ? '0' : '') + date.getDate();
    return yyyy + '-' + mm + '-' + dd;
  }

  // Parses a "yyyy-MM-dd" string (as returned by the backend for DateOnly fields) into
  // a local Date at midnight - avoids new Date(str), which treats the string as UTC and
  // can shift the calendar day under Italy's UTC+1/+2 offset.
  function parseDateISO(str) {
    var parts = str.split('-');
    return new Date(parseInt(parts[0], 10), parseInt(parts[1], 10) - 1, parseInt(parts[2], 10));
  }

  // Giorno label: full weekday + day + full month, no year (e.g. "Lunedì 17 Luglio").
  function formatGiornoLabel(date) {
    return dayFullNames[date.getDay()] + ' ' + date.getDate() + ' ' + monthNamesFull[date.getMonth()];
  }

  // "4 maggio" - day + lowercase month, no year, no weekday. Used inline in alert
  // sentences (CPU's exact phrasing: "il 4 maggio e 9 maggio").
  function formatDayMonthLowercase(date) {
    return date.getDate() + ' ' + monthNamesFull[date.getMonth()].toLowerCase();
  }

  // Settimana label: "12 - 17 Luglio" within one month, "28 Luglio - 1 Agosto" across months.
  function formatSettimanaLabel(weekStart, weekEnd) {
    if (weekStart.getMonth() === weekEnd.getMonth()) {
      return weekStart.getDate() + ' - ' + weekEnd.getDate() + ' ' + monthNamesFull[weekStart.getMonth()];
    }

    return weekStart.getDate() + ' ' + monthNamesFull[weekStart.getMonth()] + ' - ' +
      weekEnd.getDate() + ' ' + monthNamesFull[weekEnd.getMonth()];
  }

  // Piano (patient plan) row date: full weekday + DD/MM, no year (e.g. "Lunedì 03/08") -
  // the plan can span months, so DD/MM alone (unlike formatGiornoLabel's "day + full
  // month name") keeps rows compact; year deliberately omitted per CPU's direction.
  function formatPlanDate(date) {
    return dayFullNames[date.getDay()] + ' ' + formatDateDDMM(date);
  }

  function addDays(date, n) {
    var d = new Date(date);
    d.setDate(d.getDate() + n);
    return d;
  }

  // Steps by a day in the given direction (+1/-1), skipping Saturday/Sunday entirely -
  // used for Giorno's prev/next arrows only (Settimana steps by whole weeks instead).
  function addWeekdays(date, direction) {
    var d = addDays(date, direction);
    while (d.getDay() === 0 || d.getDay() === 6) {
      d = addDays(d, direction);
    }
    return d;
  }

  function getMondayOfWeek(date) {
    var d = new Date(date);
    var day = d.getDay();
    var diff = day === 0 ? -6 : 1 - day;
    d.setDate(d.getDate() + diff);
    d.setHours(0, 0, 0, 0);
    return d;
  }

  // "Vai ad Oggi" for Giorno: today, or the next Monday if today falls on a weekend.
  function getTodayForGiorno() {
    var d = new Date();
    d.setHours(0, 0, 0, 0);
    while (d.getDay() === 0 || d.getDay() === 6) {
      d = addDays(d, 1);
    }
    return d;
  }

  // Availability rules (only apply to days that actually have slots defined - a day
  // with no slots at all is simply skipped, not an error): each slot must be at least
  // 1 hour (4 slots) long, and slots within the same day must not overlap.
  function validateAvailability(availability) {
    var byDay = {};

    availability.forEach(function(slot) {
      if (!byDay[slot.dayOfWeek]) {
        byDay[slot.dayOfWeek] = [];
      }
      byDay[slot.dayOfWeek].push(slot);
    });

    for (var dayValue in byDay) {
      if (!byDay.hasOwnProperty(dayValue)) {
        continue;
      }

      var slots = byDay[dayValue].slice().sort(function(a, b) {
        return a.startTime - b.startTime;
      });

      var dayMatch = weekDays.filter(function(d) {
        return d.value === parseInt(dayValue, 10);
      });
      var dayLabel = dayMatch.length ? dayMatch[0].label : dayValue;

      for (var i = 0; i < slots.length; i++) {
        if (slots[i].endTime - slots[i].startTime < 4) {
          return 'La fascia oraria di ' + dayLabel + ' deve durare almeno 1 ora.';
        }

        if (i > 0 && slots[i].startTime < slots[i - 1].endTime) {
          return 'Le fasce orarie di ' + dayLabel + ' si sovrappongono.';
        }
      }
    }

    return null;
  }

  // Absolute start/end for the availability time picker (7:00-19:00 by default).
  // Backed by Settings/AvailabilityRange, which itself falls back to hardcoded
  // defaults server-side if the Setting rows don't exist yet (no Impostazioni
  // page to edit them from yet). Cached here so it's only fetched once.
  var availabilityRangeCache = null;

  function ensureAvailabilityRange(callback) {
    if (availabilityRangeCache) {
      callback(availabilityRangeCache);
      return;
    }

    $.get('Settings/AvailabilityRange')
      .done(function(data) {
        availabilityRangeCache = { start: data.start, end: data.end };
        callback(availabilityRangeCache);
      })
      .fail(function() {
        availabilityRangeCache = { start: 28, end: 76 }; // 07:00-19:00
        callback(availabilityRangeCache);
      });
  }

  // Generic scrollable time-picker popup: shows every 15-minute mark between the
  // availability range's start and end, about 10 rows visible (scrollable by wheel
  // or scrollbar). Clicking a row selects it; clicking anywhere outside closes the
  // popup without changing anything.
  var $openTimePicker = null;

  function closeTimePicker() {
    if ($openTimePicker) {
      $openTimePicker.remove();
      $openTimePicker = null;
    }
  }

  $(document).on('click', function() {
    closeTimePicker();
  });

  function openTimePicker($anchor, currentSlot, onSelect) {
    closeTimePicker();

    ensureAvailabilityRange(function(range) {
      var $popup = $('<div class="time-picker-popup"></div>');

      for (var slot = range.start; slot <= range.end; slot++) {
        (function(slot) {
          var $row = $('<div class="time-picker-row"></div>').text(slotToTime(slot));

          if (slot === currentSlot) {
            $row.addClass('selected');
          }

          $row.on('click', function(e) {
            e.stopPropagation();
            onSelect(slot);
            closeTimePicker();
          });

          $popup.append($row);
        })(slot);
      }

      $popup.on('click', function(e) {
        e.stopPropagation();
      });

      $('body').append($popup);

      var offset = $anchor.offset();
      $popup.css({
        top: offset.top + $anchor.outerHeight() + 4,
        left: offset.left
      });

      var $selected = $popup.find('.selected');
      if ($selected.length) {
        $popup.scrollTop($selected.position().top - ($popup.height() / 2) + ($selected.outerHeight() / 2));
      }

      $openTimePicker = $popup;
    });
  }

  // All elements that should start hidden are hidden here via jQuery,
  // not via the HTML "hidden" attribute or CSS, to avoid the two mechanisms
  // fighting each other. From this point on, only .show()/.hide() control visibility.
  $('#app-view').hide();
  $('#gestione-section').hide();
  $('#login-error').hide();
  $('#modal-overlay').hide();
  $('#account-menu-dropdown').hide();
  $('#topbar-fallback').hide();
  $('#print-button').hide();

  // Set once per login by showApp() below - lets code anywhere (e.g. the Giorno/
  // Settimana slot detail popup) check the current user's role/id without an extra
  // round-trip to App/me.
  var currentSession = null;

  // Tracks the TherapySlot.Id currently being dragged in the Giorno/Settimana grid -
  // see buildScheduleGridTable's drag handlers below. Only ever set while a native
  // HTML5 drag is in progress.
  var draggedGiornoSettimanaSlotId = null;

  // The jQuery-wrapped overlay <div> being dragged, and whichever cell is currently
  // highlighted as a drag-over target - both null outside an active drag. Kept apart
  // from draggedGiornoSettimanaSlotId (which is the semantic "what's being moved")
  // since these two are purely presentational (slide-back animation + highlight).
  var draggedGiornoSettimanaOverlay = null;
  var $giornoSettimanaDragOverCell = null;

  // True while the Ripianifica draft overlay itself (not a real slot) is being
  // dragged - the drop handler branches on this to just reposition ripianificaDraft
  // locally instead of calling Giorno/Slot/{id}/Move.
  var draggedRipianificaDraft = false;

  function clearGiornoSettimanaDragOverHighlight() {
    if ($giornoSettimanaDragOverCell) {
      $giornoSettimanaDragOverCell.removeClass('giorno-settimana-cell-dragover');
      $giornoSettimanaDragOverCell = null;
    }
  }

  function showApp(session) {
    currentSession = session;
    $('#login-view').hide();
    $('#app-view').show();
    $('#account-menu-button').text(session.name);

    if (session.isAccettazione) {
      $('#gestione-section').show();
      $('.nav-item[data-view="mese"]').show();
      $('#toggle-removed').show();
      $('#toggle-audit').show();
    } else {
      $('#gestione-section').hide();
      $('.nav-item[data-view="mese"]').hide();
      $('#toggle-removed').hide();
      $('#toggle-audit').hide();
    }

    var parts = window.location.hash.replace('#', '').split('/');
    var view = parts[0] || 'giorno';
    var id = parseHashId(parts[1]);

    // Guarantee a "floor" history entry at the default view underneath whatever the app
    // was opened on. Covers deep links, bookmarks, or a page refresh landing mid-flow:
    // without this, pressing Back from there would leave the app entirely instead of
    // landing on Giorno. replaceState/pushState don't fire hashchange, so no extra
    // render happens here - navigateTo() below picks it up normally.
    if (view !== 'giorno' || id) {
      history.replaceState(null, '', '#giorno');
      history.pushState(null, '', '#' + view + (id ? '/' + id : ''));
    }

    navigateTo(view, id);

    $('#welcome-greeting-text').text('Ciao ' + session.name + '!');
    $('#welcome-overlay').css('display', 'flex');
  }

  function showLogin() {
    currentSession = null;
    $('#app-view').hide();
    $('#login-view').show();
  }

  function checkSession() {
    $.get('App/me')
      .done(function(session) {
        showApp(session);
      })
      .fail(function() {
        showLogin();
      });
  }

  // Global session-expiry handling: any 401 from any AJAX call (list, save, remove, etc.)
  // means the cookie session is gone - drop back to the login view instead of the request
  // silently failing, which is what was happening before (e.g. clicking Terapie doing nothing).
  $(document).ajaxError(function(event, jqXHR) {
    if (jqXHR.status === 401) {
      showLogin();
    }
  });

  $('#login-form').on('submit', function(e) {
    e.preventDefault();

    var payload = {
      name: $('#login-name').val(),
      password: $('#login-password').val()
    };

    $('#login-error').hide();

    $.ajax({
      url: 'App/login',
      method: 'POST',
      contentType: 'application/json',
      data: JSON.stringify(payload)
    })
      .done(function() {
        checkSession();
      })
      .fail(function() {
        $('#login-error').show();
      });
  });

  $('#logout-button').on('click', function() {
    $.post('App/logout', function() {
      showLogin();
    });
  });

  $('#welcome-continue-button').on('click', function() {
    $('#welcome-overlay').hide();
  });

  $('#welcome-switch-user-button').on('click', function() {
    $('#welcome-overlay').hide();
    $.post('App/logout', function() {
      showLogin();
    });
  });

  // Account menu (top-right dropdown)
  $('#account-menu-button').on('click', function(e) {
    e.stopPropagation();
    $('#account-menu-dropdown').toggle();
  });

  $('#print-button').on('click', function() {
    printSourceView = currentView; // 'giorno' or 'settimana' - see loadView's visibility check
    navigateTo('stampa');
  });

  $(document).on('click', function(e) {
    if (!$(e.target).closest('#account-menu').length) {
      $('#account-menu-dropdown').hide();
    }
  });

  function reloadCurrentList() {
    if (currentView === 'utenti') {
      renderUtentiList();
    } else if (currentView === 'terapie') {
      renderTerapieList();
    } else if (currentView === 'impostazioni') {
      renderImpostazioniList();
    } else if (currentView === 'pacchettiTariffario') {
      renderPacchettoTariffarioPage();
    } else if (currentView === 'presenze') {
      buildPresenzeView();
    } else if (currentView === 'pacchetti') {
      renderPacchettiList();
    }
  }

  $('#toggle-removed').on('click', function() {
    showRemoved = !showRemoved;
    $(this).toggleClass('active-toggle', showRemoved);
    $('#account-menu-dropdown').hide();
    reloadCurrentList();
  });

  $('#toggle-audit').on('click', function() {
    showAudit = !showAudit;
    $(this).toggleClass('active-toggle', showAudit);
    $('#account-menu-dropdown').hide();
    reloadCurrentList();
  });

  // "Mostra informazioni celle" - gates the capacity debug popup on empty Giorno/
  // Settimana cells (see buildScheduleGridTable). Doesn't need a list reload like
  // the two toggles above; it only affects click behavior on the grid, not what's
  // fetched, so nothing needs to re-render when it's flipped.
  $('#toggle-capacity-debug').on('click', function() {
    showCapacityDebug = !showCapacityDebug;
    $(this).toggleClass('active-toggle', showCapacityDebug);
    $('#account-menu-dropdown').hide();
  });

  // Sidebar navigation
  $('#sidebar-nav').on('click', '.nav-item', function() {
    navigateTo($(this).data('view'));
  });

  // Back-button / view history: the URL hash is the source of truth for "where we are".
  // navigateTo() pushes a new hash entry; the browser's own back/forward then just changes
  // the hash, which we pick up here and re-render from. suppressHashChange avoids re-rendering
  // when *we* are the one who just set the hash (navigateTo already rendered).
  //
  // replace=true swaps the current entry in place instead of stacking a new one - used when
  // a transient state (the "new" form) turns into a real one (a saved id) after Save, so
  // Back lands on the list in one press instead of bouncing through an empty "new" form.
  // replaceState never fires hashchange, so suppressHashChange isn't needed for that path.
  function navigateTo(view, id, replace) {
    removeEdgeOverlays(); // safety net - these are appended to <body>, not #content

    var hash = '#' + view + (id ? '/' + id : '');

    if (window.location.hash === hash) {
      loadView(view, id);
      return;
    }

    if (replace) {
      history.replaceState(null, '', hash);
    } else {
      suppressHashChange = true;
      window.location.hash = hash;
    }

    loadView(view, id);
  }

  // id in the hash is either omitted (list view), the literal "new" (create form),
  // or a numeric id (edit form) - kept distinct so "New" and the list each get their
  // own history entry and the back button lands somewhere sensible.
  function parseHashId(raw) {
    if (!raw) {
      return null;
    }

    return raw === 'new' ? 'new' : parseInt(raw, 10);
  }

  $(window).on('hashchange', function() {
    if (suppressHashChange) {
      suppressHashChange = false;
      return;
    }

    var parts = window.location.hash.replace('#', '').split('/');
    var view = parts[0] || 'giorno';
    var id = parseHashId(parts[1]);

    loadView(view, id);
  });

  function loadView(view, id) {
    currentView = view;

    $('.nav-item').removeClass('active');
    $('.nav-item[data-view="' + view + '"]').addClass('active');

    setTopbarActions(null);

    // Only ever set while on the Stampa or Piano views (see setStampaPageOrientation/
    // applyStampaFontSizing) - torn down here so a leftover orientation/font override
    // never bleeds into printing some other page later.
    if (view !== 'stampa' && view !== 'piano') {
      $('#stampa-page-style').remove();
      $('#stampa-font-style').remove();
    }

    // Only Giorno/Settimana are printable for now - hidden everywhere else, including
    // on the print preview page itself (it has its own Stampa/Chiudi buttons instead).
    if (view === 'giorno' || view === 'settimana') {
      $('#print-button').show();
    } else {
      $('#print-button').hide();
    }

    if (view === 'giorno') {
      renderGiornoView();
    } else if (view === 'presenze') {
      renderPresenzeView();
    } else if (view === 'settimana') {
      renderSettimanaView();
    } else if (view === 'mese') {
      renderMeseView();
    } else if (view === 'stampa') {
      renderStampaView();
    } else if (view === 'utenti') {
      if (id === 'new') {
        renderUtentiForm(null);
      } else if (id) {
        renderUtentiForm(id);
      } else {
        renderUtentiList();
      }
    } else if (view === 'terapie') {
      if (id === 'new') {
        renderTerapiaForm(null);
      } else if (id) {
        renderTerapiaForm(id);
      } else {
        renderTerapieList();
      }
    } else if (view === 'impostazioni') {
      renderImpostazioniList();
    } else if (view === 'assenze') {
      if (id === 'new') {
        renderAssenzaForm(null);
      } else if (id) {
        renderAssenzaForm(id);
      } else {
        renderAssenzeList();
      }
    } else if (view === 'pazienti') {
      if (id === 'new') {
        renderPazienteForm(null);
      } else if (id) {
        renderPazienteForm(id);
      } else {
        renderPazientiList();
      }
    } else if (view === 'piano') {
      renderPianoView(id);
    } else if (view === 'avvisi') {
      renderAvvisiList();
    } else if (view === 'pianificazione') {
      renderPianificazioneView(id);
    } else if (view === 'pianificazioneReparto') {
      renderPianificazioneRepartoView(id);
    } else if (view === 'pianificazioneMixed') {
      renderPianificazioneMixedView(id);
    } else if (view === 'pacchetti') {
      if (id === 'new') {
        renderPacchettoForm(null);
      } else if (id) {
        renderPacchettoForm(id);
      } else {
        renderPacchettiList();
      }
    } else if (view === 'pacchettiTariffario') {
      renderPacchettoTariffarioPage();
    } else if (view === 'gestisciAssenza') {
      renderGestisciAssenzaView();
    } else {
      $('#content').text('Vista: ' + view + ' (non ancora implementata)');
    }
  }

  function formatDate(isoString) {
    if (!isoString) {
      return '';
    }

    var d = new Date(isoString);
    return d.toLocaleDateString('it-IT') + ' ' + d.toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' });
  }

  function intToHexColor(value) {
    var hex = (value >>> 0).toString(16).padStart(6, '0');
    return '#' + hex;
  }

  function hexColorToInt(hex) {
    return parseInt(hex.replace('#', ''), 16);
  }

  // Half-brightness version of a hex color, used for the Giorno Reparto-colored
  // overlay border (same hue, darker) - floors each channel to avoid rounding up.
  function halfBrightnessHex(hex) {
    var r = Math.floor(parseInt(hex.substr(1, 2), 16) / 2);
    var g = Math.floor(parseInt(hex.substr(3, 2), 16) / 2);
    var b = Math.floor(parseInt(hex.substr(5, 2), 16) / 2);
    return '#' + [r, g, b].map(function(v) { return v.toString(16).padStart(2, '0'); }).join('');
  }

  // Generic remove-confirmation modal, reused by any module with a Rimuovi action.
  function showRemoveModal(options, errorMessage) {
    var errorHtml = errorMessage ? '<div class="modal-error">' + errorMessage + '</div>' : '';
    var bodyHtml =
      '<p>Per confermare la rimozione, digita nuovamente il nome <strong>' + options.name + '</strong>:</p>' +
      '<input type="text" id="remove-confirm-input">' +
      errorHtml;

    window.showModal(bodyHtml, [
      {
        label: 'Rimuovi',
        className: 'danger',
        onClick: function() {
          var typed = $('#remove-confirm-input').val();

          $.ajax({
            url: options.endpoint + '/Remove/' + options.id,
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ confirmName: typed })
          })
            .done(function() {
              options.onSuccess();
            })
            .fail(function(jqXHR) {
              var message = (jqXHR.responseJSON && jqXHR.responseJSON.message)
                ? jqXHR.responseJSON.message
                : 'Il nome digitato non corrisponde.';
              showRemoveModal(options, message);
            });
        }
      },
      {
        label: 'Annulla',
        className: 'secondary',
        onClick: function() {}
      }
    ]);
  }

  var therapyTypesCache = null;

  // "Cancella tutte le sedute future" - same type-the-name strong confirmation as
  // showRemoveModal, but hits Therapies/{id}/CancelFutureSlots (only future,
  // not-yet-occurred slots are removed - the Therapy itself and past slots stay).
  function showCancelFutureSlotsModal(therapyId, patientName, fromDate, errorMessage) {
    var errorHtml = errorMessage ? '<div class="modal-error">' + errorMessage + '</div>' : '';
    var bodyHtml =
      '<p>Verranno eliminate tutte le sedute future non ancora svolte di <strong>' + patientName + '</strong>.</p>' +
      '<p>Per confermare, digita nuovamente il nome <strong>' + patientName + '</strong>:</p>' +
      '<input type="text" id="cancel-future-confirm-input">' +
      errorHtml;

    window.showModal(bodyHtml, [
      {
        label: 'Cancella sedute future',
        className: 'danger',
        onClick: function() {
          var typed = $('#cancel-future-confirm-input').val();

          $.ajax({
            url: 'Therapies/' + therapyId + '/CancelFutureSlots',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ confirmName: typed, fromDate: fromDate })
          })
            .done(function() {
              refreshGiornoSettimanaView();
            })
            .fail(function(jqXHR) {
              var message = (jqXHR.responseJSON && jqXHR.responseJSON.message)
                ? jqXHR.responseJSON.message
                : 'Il nome digitato non corrisponde.';
              showCancelFutureSlotsModal(therapyId, patientName, fromDate, message);
            });
        }
      },
      {
        label: 'Annulla',
        className: 'secondary',
        onClick: function() {}
      }
    ]);
  }

  function loadTherapyTypes(callback) {
    if (therapyTypesCache) {
      callback();
      return;
    }

    $.get('TherapyTypes/List').done(function(list) {
      therapyTypesCache = list;
      callback();
    });
  }

  // --- Giorno / Settimana (scheduling scaffolding) --------------------------

  function loadSchedulingTherapists(callback) {
    if (schedulingTherapists) {
      callback();
      return;
    }

    $.get('Users/TherapistsForScheduling').done(function(list) {
      schedulingTherapists = list;
      callback();
    });
  }

  // Does this vacation row cover the given date? Returns null (no match) or
  // { ampm, label } - ampm is null (full day), 0 (Mattina) or 1 (Pomeriggio).
  function findVacationForDate(vacations, dateObj, therapistName) {
    var dateStr = formatDateISO(dateObj);
    var month = dateObj.getMonth() + 1;
    var day = dateObj.getDate();
    var match = null;

    vacations.forEach(function(v) {
      if (match) {
        return;
      }

      if (v.isYearIndependent === 1 && v.month === month && v.day === day) {
        match = { ampm: null, label: v.name };
      } else if (v.startDate && v.endDate && dateStr >= v.startDate && dateStr <= v.endDate) {
        var isSingleDay = v.startDate === v.endDate;
        match = {
          ampm: isSingleDay ? v.ampm : null,
          label: v.name ? v.name : ('Assenza ' + therapistName)
        };
      }
    });

    return match;
  }

  // Turns a vacation match into a { coverStart, coverEnd } slot range within
  // [minStart, maxEnd) - split at 13:00 for half-day Assenze, full range otherwise.
  function vacationCoverage(match, minStart, maxEnd) {
    if (!match) {
      return null;
    }

    var noonSlot = timeToSlot('13:00');
    var coverStart = minStart;
    var coverEnd = maxEnd;

    if (match.ampm === 0) {
      coverEnd = Math.min(maxEnd, noonSlot);
    } else if (match.ampm === 1) {
      coverStart = Math.max(minStart, noonSlot);
    }

    return { coverStart: coverStart, coverEnd: coverEnd, label: match.label };
  }

  // Per-slot status for one Settimana day-column: vacation overlay takes priority,
  // otherwise anything outside the day's own TherapistAvailability ranges (which can
  // be more than one - e.g. a lunch-break gap) is flagged as plain non-availability
  // (same pink treatment, no label).
  function computeColumnSlots(dayAvailability, vacationMatch, minStart, maxEnd) {
    var vacRange = vacationCoverage(vacationMatch, minStart, maxEnd);
    var slots = [];

    for (var slot = minStart; slot < maxEnd; slot++) {
      if (vacRange && slot >= vacRange.coverStart && slot < vacRange.coverEnd) {
        slots.push({ type: 'vacation', label: vacRange.label });
        continue;
      }

      var isAvailable = dayAvailability.some(function(a) {
        return slot >= a.startTime && slot < a.endTime;
      });

      slots.push(isAvailable ? { type: null, label: null } : { type: 'unavailable', label: null });
    }

    return slots;
  }

  function renderPresenzeView() {
    if (!presenzeCurrentDate) {
      presenzeCurrentDate = getTodayForGiorno();
    }
    // Accettazione users have a Therapist row too (they're in schedulingTherapists),
    // but Presenze is for actual therapists marking their own day - only default to
    // the logged-in user when they're not Accettazione, otherwise leave it blank so
    // "Scegli terapista" shows instead of silently 404-ing on load.
    if (!presenzeMode && !presenzeSelectedTherapistId && currentSession && !currentSession.isAccettazione) {
      presenzeMode = 'therapist';
      presenzeSelectedTherapistId = currentSession.id;
    }

    loadSchedulingTherapists(function() {
      buildPresenzeView();
    });
  }

  function buildPresenzeHeader() {
    var $header = $('<div class="scheduling-header"></div>');

    var state = {
      mode: presenzeMode,
      therapistId: presenzeSelectedTherapistId,
      repartoSex: presenzeSelectedRepartoSex
    };

    var $selector = buildEntitySelector(state, function() {
      presenzeMode = state.mode;
      presenzeSelectedTherapistId = state.therapistId;
      presenzeSelectedRepartoSex = state.repartoSex;
      buildPresenzeView();
    });

    var today = getTodayForGiorno();
    var isToday = formatDateISO(presenzeCurrentDate) === formatDateISO(today);

    var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
    $prev.on('click', function() {
      presenzeCurrentDate = addWeekdays(presenzeCurrentDate, -1);
      buildPresenzeView();
    });

    var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
    $next.prop('disabled', isToday);
    $next.on('click', function() {
      if (isToday) {
        return;
      }
      presenzeCurrentDate = addWeekdays(presenzeCurrentDate, 1);
      buildPresenzeView();
    });

    var $dateLabel = $('<span class="scheduling-date-label scheduling-date-label-clickable"></span>').text(formatGiornoLabel(presenzeCurrentDate));
    $dateLabel.on('click', function() {
      showQuickDatePicker(presenzeCurrentDate, function(pickedDate) {
        var todayCheck = getTodayForGiorno();
        presenzeCurrentDate = pickedDate > todayCheck ? todayCheck : pickedDate;
        buildPresenzeView();
      });
    });

    var $today = $('<button type="button" class="secondary">Vai ad Oggi</button>');
    $today.on('click', function() {
      presenzeCurrentDate = getTodayForGiorno();
      buildPresenzeView();
    });

    $header
      .append($selector)
      .append($prev).append($dateLabel).append($next).append($today);
    return $header;
  }

  function presenzeIconHtml(status) {
    if (status === 1) {
      return '<span style="background-color:green;border: 1px solid black;">✔</span>';
    }
    if (status === 2) {
      return '<span style="background-color:orange;border: 1px solid black;">✖</span>';
    }
    return '<span style="background-color:orange;border: 1px solid black;">?</span>';
  }

  function presenzeTitleText(patientName, status) {
    return patientName + ' - ' + slotStatusLabels[status];
  }

  function presenzeLabelHtml(patientName, therapyTypeLabel, status) {
    return presenzeIconHtml(status) + ' ' + patientName + ' - ' + therapyTypeLabel;
  }

  function presenzeAuditLineHtml(modUserName, modDate) {
    if (!modUserName || !modDate) {
      return '';
    }
    var d = new Date(modDate);
    var stamp = formatDateDDMM(d) + ' ' + (d.getHours() < 10 ? '0' : '') + d.getHours() + ':' + (d.getMinutes() < 10 ? '0' : '') + d.getMinutes();
    return modUserName + ' - ' + stamp;
  }

  // Mirrors buildScheduleGridTable's visual structure (same table/row/overlay
  // classes, same time column, same overlap-group width-splitting so several
  // patients at the same timeSlot sit side by side in one row) but click cycles
  // attendance status instead of opening the slot popup, and there's no drag.
  // Each slot is double-height: the top half is the usual icon/name/therapy label,
  // the bottom half shows who last changed it and when (CPU's audit call).
  function buildPresenzeGridTable(data) {
    var gridStart = data.gridStart;
    var gridEnd = data.gridEnd;
    var rowCount = gridEnd - gridStart;
    var rowHeightPx = PIANIFICAZIONE_ROW_HEIGHT;

    var $wrapper = $('<div class="scheduling-view pianificazione-wrapper"></div>');
    var $table = $('<table class="scheduling-grid pianificazione-grid"></table>');
    $table.append('<thead><tr><th>Orario</th><th>' + data.name + '</th></tr></thead>');

    var vacationMatch = findVacationForDate(data.vacations, presenzeCurrentDate, data.name);
    var availability = data.availability && data.availability.length > 0 ? data.availability : [{ startTime: gridStart, endTime: gridEnd }];
    var overlaySlots = computeColumnSlots(availability, vacationMatch, gridStart, gridEnd);

    var items = data.slots.map(function(s) {
      return {
        key: 'slot-' + s.id,
        id: s.id,
        startSlot: s.timeSlot,
        endSlot: s.timeSlot + s.durationSlots,
        patientName: s.patientName,
        therapyTypeLabel: s.therapyTypeLabel,
        color: s.therapyTypeColor,
        status: s.status,
        modUserName: s.modUserName,
        modDate: s.modDate
      };
    });
    var groups = computeOverlapGroups(items);

    var $tbody = $('<tbody></tbody>');
    var tdRefs = [];

    for (var row = 0; row < rowCount; row++) {
      var slotVal = gridStart + row;
      var $row = $('<tr></tr>');
      $row.append('<td class="scheduling-time-cell" style="height:' + rowHeightPx + 'px">' + slotToTime(slotVal) + '</td>');

      var overlayInfo = overlaySlots[row];
      var prevInfo = row > 0 ? overlaySlots[row - 1] : null;
      var isRunStart = !prevInfo || prevInfo.type !== overlayInfo.type || prevInfo.label !== overlayInfo.label;
      var overlayClass = overlayInfo.type === 'vacation' ? ' vacation-overlay' :
        (overlayInfo.type === 'unavailable' ? ' availability-overlay' : '');

      var $td = $('<td class="giorno-settimana-cell' + overlayClass + '" style="height:' + rowHeightPx + 'px"></td>');

      if (overlayInfo.type === 'vacation' && isRunStart) {
        $td.append('<span class="giorno-vacation-label">' + overlayInfo.label + '</span>');
      }

      $row.append($td);
      tdRefs[row] = $td;
      $tbody.append($row);
    }

    $table.append($tbody);

    items.forEach(function(item) {
      var rowIndex = item.startSlot - gridStart;
      if (rowIndex < 0 || rowIndex >= rowCount) {
        return;
      }

      var span = item.endSlot - item.startSlot;
      var groupInfo = groups[item.key];
      var groupSize = groupInfo.groupSize;
      var idx = groupInfo.indexInGroup;

      var $overlay = $('<div class="pianificazione-session-overlay pianificazione-reparto-overlay presenze-overlay" title="' + presenzeTitleText(item.patientName, item.status) + '"><span class="pianificazione-cell-label"></span><span class="presenze-audit-line"></span></div>');
      $overlay.find('.pianificazione-cell-label').html(presenzeLabelHtml(item.patientName, item.therapyTypeLabel, item.status));
      if (showAudit) {
        $overlay.find('.presenze-audit-line').text(presenzeAuditLineHtml(item.modUserName, item.modDate));
      }
      $overlay.css({
        top: '0',
        height: (span * rowHeightPx * (showAudit ? 2 : 1) - 1) + 'px',
        left: 'calc((100% - 2em) * ' + idx + ' / ' + groupSize + ')',
        width: 'calc((100% - 2em) / ' + groupSize + ')'
      });

      var hex = intToHexColor(item.color);
      $overlay.css({
        background: hex,
        border: '1px solid ' + halfBrightnessHex(hex)
      });

      var $startTd = tdRefs[rowIndex];
      if ($startTd) {
        $startTd.append($overlay);
      }

      $overlay.on('click', function(e) {
        e.stopPropagation();
        $.ajax({ url: 'Presenze/Slot/' + item.id + '/CycleStatus', method: 'POST' }).done(function(result) {
          item.status = result.status;
          item.modUserName = result.modUserName;
          item.modDate = result.modDate;
          $overlay.attr('title', presenzeTitleText(item.patientName, item.status));
          $overlay.find('.pianificazione-cell-label').html(presenzeLabelHtml(item.patientName, item.therapyTypeLabel, item.status));
          if (showAudit) {
            $overlay.find('.presenze-audit-line').text(presenzeAuditLineHtml(item.modUserName, item.modDate));
          }
        });
      });
    });

    $wrapper.append($table);
    return $wrapper;
  }

  function buildPresenzeView() {
    var $header = buildPresenzeHeader();
    setTopbarActions($header);

    abortIfActive(presenzeActiveRequest);

    if (!presenzeMode) {
      $('#content').empty().append('<div class="scheduling-empty">Scegli terapista</div>');
      return;
    }

    var request = presenzeMode === 'reparto'
      ? $.get('Presenze/RepartoData', { sex: presenzeSelectedRepartoSex, date: formatDateISO(presenzeCurrentDate) })
      : $.get('Presenze/TherapistData', { therapistId: presenzeSelectedTherapistId, date: formatDateISO(presenzeCurrentDate) });

    presenzeActiveRequest = request;

    request
      .done(function(data) {
        if (!data.hasAvailability) {
          $('#content').empty().append('<div class="scheduling-empty">Nessuna disponibilità per il terapista ' + data.name + '</div>');
          return;
        }

        var $wrapper = buildPresenzeGridTable(data);
        $('#content').empty().append($wrapper);
      })
      .fail(function(jqXHR) {
        if (jqXHR.statusText === 'abort') {
          return; // intentionally cancelled by a newer navigation - not a real failure
        }
        $('#content').empty().append('<div class="scheduling-empty">Scegli terapista</div>');
      });
  }

  function renderMeseView() {
    if (!meseCurrentYear) {
      var today = getTodayForGiorno();
      meseCurrentYear = today.getFullYear();
      meseCurrentMonth = today.getMonth() + 1;
    }

    loadSchedulingTherapists(function() {
      buildMeseView();
    });
  }

  function buildMeseView() {
    var $header = $('<div class="scheduling-header"></div>');

    var $therapistSelect = $('<select class="scheduling-therapist-select"></select>');
    $therapistSelect.append('<option value="">Tutti (globale)</option>');
    schedulingTherapists.forEach(function(t) {
      $therapistSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
    });
    $therapistSelect.val(meseSelectedTherapistId || '');
    $therapistSelect.on('change', function() {
      meseSelectedTherapistId = $therapistSelect.val() ? parseInt($therapistSelect.val(), 10) : null;
      buildMeseView();
    });

    var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
    $prev.on('click', function() {
      meseCurrentMonth -= 1;
      if (meseCurrentMonth < 1) {
        meseCurrentMonth = 12;
        meseCurrentYear -= 1;
      }
      buildMeseView();
    });

    var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
    $next.on('click', function() {
      meseCurrentMonth += 1;
      if (meseCurrentMonth > 12) {
        meseCurrentMonth = 1;
        meseCurrentYear += 1;
      }
      buildMeseView();
    });

    var $label = $('<span class="scheduling-date-label"></span>').text(monthNamesFull[meseCurrentMonth - 1] + ' ' + meseCurrentYear);

    $header.append($therapistSelect).append($prev).append($label).append($next);
    setTopbarActions($header);

    $.get('Mese/Data', { year: meseCurrentYear, month: meseCurrentMonth, therapistId: meseSelectedTherapistId })
      .done(function(result) {
        var $wrapper = buildMeseGridTable(result);
        $('#content').empty().append($wrapper);
      });
  }

  // Red (<=10%) -> yellow (25%) -> green (>=50%), smooth blend between stops.
  function meseGradientColor(percent) {
    var red = [192, 57, 43];
    var yellow = [241, 196, 15];
    var green = [39, 174, 96];

    function mix(c1, c2, t) {
      return [
        Math.round(c1[0] + (c2[0] - c1[0]) * t),
        Math.round(c1[1] + (c2[1] - c1[1]) * t),
        Math.round(c1[2] + (c2[2] - c1[2]) * t)
      ];
    }

    var rgb;
    if (percent <= 10) {
      rgb = red;
    } else if (percent <= 25) {
      rgb = mix(red, yellow, (percent - 10) / 15);
    } else if (percent <= 50) {
      rgb = mix(yellow, green, (percent - 25) / 25);
    } else {
      rgb = green;
    }

    return 'rgb(' + rgb[0] + ',' + rgb[1] + ',' + rgb[2] + ')';
  }

  function buildMeseGridTable(result) {
    var dataByDate = {};
    result.days.forEach(function(d) { dataByDate[d.date] = d; });

    var firstOfMonth = new Date(meseCurrentYear, meseCurrentMonth - 1, 1);
    var lastOfMonth = new Date(meseCurrentYear, meseCurrentMonth, 0);

    var gridStart = getMondayOfWeek(firstOfMonth);
    var gridEnd = getMondayOfWeek(lastOfMonth);
    gridEnd.setDate(gridEnd.getDate() + 4); // Friday of the last week

    // Own class only, NOT data-table - that class's row-hover highlights the whole
    // row, which makes no sense for a calendar grid (CPU's call).
    var $table = $('<table class="mese-table"></table>');
    $table.append('<thead><tr><th>Lun</th><th>Mar</th><th>Mer</th><th>Gio</th><th>Ven</th></tr></thead>');
    var $tbody = $('<tbody></tbody>');

    var cursor = new Date(gridStart);
    while (cursor <= gridEnd) {
      var $row = $('<tr></tr>');

      for (var i = 0; i < 5; i++) {
        var inMonth = cursor.getMonth() === meseCurrentMonth - 1;
        var $cell = $('<td class="mese-cell"></td>');

        if (inMonth) {
          var dateStr = formatDateISO(cursor);
          var dayData = dataByDate[dateStr];
          var vacMatch = findVacationForDate(result.globalVacations, cursor, '');

          $cell.append($('<div class="mese-cell-daynum"></div>').text(cursor.getDate()));

          if (vacMatch) {
            $cell.addClass('mese-cell-vacation-bg');
            $cell.append($('<div class="mese-cell-vacation"></div>').text(vacMatch.label));
          } else if (dayData && dayData.numB > 0) {
            var pct = Math.round((dayData.numA / dayData.numB) * 100);
            $cell.css('background', meseGradientColor(pct));
          }

          if (dayData && !vacMatch) {
            var pctLabel = dayData.numB > 0 ? Math.round((dayData.numA / dayData.numB) * 100) : 0;
            $cell.append($('<div class="mese-cell-counts"></div>').text(dayData.numA + ' / ' + dayData.numB + ' (' + pctLabel + '%)'));
          }

          (function(clickDate) {
            $cell.on('click', function() {
              giornoCurrentDate = clickDate;
              if (meseSelectedTherapistId) {
                giornoMode = 'therapist';
                giornoSelectedTherapistId = meseSelectedTherapistId;
              }
              navigateTo('giorno');
            });
          })(new Date(cursor));
        } else {
          $cell.addClass('mese-cell-outside');
        }

        $row.append($cell);
        cursor.setDate(cursor.getDate() + 1);
      }

      cursor.setDate(cursor.getDate() + 2); // skip Sat/Sun - land back on the following Monday
      $tbody.append($row);
    }

    $table.append($tbody);
    return $table;
  }

  // "Gestisci assenza" - urgent same-day tool: pick the therapist who called in,
  // report it, and get the full list of everyone they had booked today (name,
  // phone, each slot) to call and inform, with a one-click reschedule shortcut
  // that reuses the existing Ripianifica flow exactly (same conflict rules, same
  // Settimana-based propose/confirm UI) and returns here afterward.
  function renderGestisciAssenzaView() {
    loadSchedulingTherapists(function() {
      buildGestisciAssenzaView();
    });
  }

  function buildGestisciAssenzaView() {
    var $header = $('<div class="scheduling-header"></div>');

    var $select = $('<select></select>');
    $select.append('<option value="">Seleziona terapista...</option>');
    schedulingTherapists.forEach(function(t) {
      $select.append('<option value="' + t.id + '">' + t.name + '</option>');
    });

    var $reportBtn = $('<button type="button">Inserisci assenza</button>');
    $reportBtn.on('click', function() {
      var therapistId = parseInt($select.val(), 10);
      if (!therapistId) {
        return;
      }

      $.ajax({
        url: 'GestisciAssenza/Report',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({ therapistId: therapistId })
      }).done(function(result) {
        if (result.alreadyAbsent) {
          window.showModal('<p>' + result.message + '</p>', [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
        }
        loadGestisciAssenzaList();
      });
    });

    $header.append($select).append($reportBtn);
    setTopbarActions($header);

    loadGestisciAssenzaList();
  }

  function loadGestisciAssenzaList() {
    $.get('GestisciAssenza/List').done(function(result) {
      if (result.length === 0) {
        $('#content').empty().append('<div class="scheduling-empty">Nessuna assenza urgente segnalata oggi.</div>');
        return;
      }

      // One row per PATIENT, not per slot - if a patient has more than one slot
      // today, only the earliest is shown and the rest are dropped from this list
      // entirely (CPU's call: they only need to be called once). Fixed-width,
      // nowrap columns (see CSS) so this stays readable even with 40+ rows.
      var $table = $('<table class="data-table gestisci-assenza-table"></table>');
      $table.append('<thead><tr><th>Ora</th><th>Paziente</th><th>Telefono</th><th>Terapia</th><th>Ripianifica</th><th>Chiamato</th></tr></thead>');
      var $tbody = $('<tbody></tbody>');

      result.forEach(function(t) {
        $tbody.append('<tr class="gestisci-assenza-therapist-row"><td colspan="6">' + t.therapistName + '</td></tr>');

        t.patients.forEach(function(p) {
          if (p.slots.length === 0) {
            return;
          }

          var s = p.slots[0];
          var $row = $('<tr></tr>');

          $row.append($('<td></td>').text(slotToTime(s.timeSlot)));
          $row.append($('<td></td>').text(p.patientName));
          $row.append($('<td></td>').text(formatPhoneDisplay(p.phone)));
          $row.append($('<td></td>').text(s.therapyTypeLabel));

          if (s.rescheduledToDate) {
            var rDate = parseDateISO(s.rescheduledToDate);
            var label = 'Ripianificato al ' + dayFullNames[rDate.getDay()] + ' ' + rDate.getDate() + '/' + (rDate.getMonth() + 1);
            $row.append($('<td></td>').text(label));
          } else {
            var $ripianificaBtn = $('<button type="button" class="secondary">Ripianifica</button>');
            $ripianificaBtn.on('click', function() {
              $.get('Giorno/Slot/' + s.slotId + '/RipianificaPropose')
                .done(function(proposal) {
                  ripianificaDraft = {
                    originalSlotId: proposal.originalSlotId,
                    therapyPartId: proposal.therapyPartId,
                    therapistId: proposal.therapistId,
                    therapistName: proposal.therapistName,
                    patientName: proposal.patientName,
                    therapyTypeName: proposal.therapyTypeName,
                    therapyTypeColor: proposal.therapyTypeColor,
                    durationSlots: proposal.durationSlots,
                    date: proposal.proposedDate,
                    timeSlot: proposal.proposedTimeSlot
                  };
                  ripianificaReturnView = 'gestisciAssenza';
                  settimanaMode = 'therapist';
                  settimanaSelectedTherapistId = proposal.therapistId;
                  settimanaCurrentWeekStart = getMondayOfWeek(parseDateOnly(proposal.proposedDate));
                  navigateTo('settimana');
                })
                .fail(function(jqXHR) {
                  var msg = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Impossibile calcolare una proposta.';
                  window.showModal('<p>' + msg + '</p>', [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
                });
            });
            $row.append($('<td></td>').append($ripianificaBtn));
          }

          var $checkbox = $('<input type="checkbox">').prop('checked', p.called);
          $checkbox.on('change', function() {
            $.ajax({
              url: 'GestisciAssenza/MarkCalled',
              method: 'POST',
              contentType: 'application/json',
              data: JSON.stringify({ therapistId: t.therapistId, patientId: p.patientId, called: $checkbox.is(':checked') })
            });
          });
          $row.append($('<td></td>').append($checkbox));

          $tbody.append($row);
        });
      });

      $table.append($tbody);
      $('#content').empty().append($table);
    });
  }

  function renderGiornoView() {
    if (!giornoCurrentDate) {
      giornoCurrentDate = getTodayForGiorno();
    }

    loadSchedulingTherapists(function() {
      buildGiornoView();
    });
  }

  function giornoCapacityColorClass(capacity, demand, warningThresholdPercent) {
    var free = capacity - demand;
    if (free <= 0) {
      return 'pianificazione-capacity-red';
    }
    var occupancyRatio = capacity > 0 ? demand / capacity : 1;
    var thresholdRatio = (warningThresholdPercent || 75) / 100;
    if (occupancyRatio >= thresholdRatio) {
      return 'pianificazione-capacity-yellow';
    }
    return '';
  }

  // Therapist select + Reparto options, mutually exclusive - shared by both Giorno
  // and Settimana headers. `state` is a plain { mode, therapistId, repartoSex }
  // object mutated in place; onChange() is called after every selection so the
  // caller can sync it back into its own (Giorno- or Settimana-specific) globals
  // and rebuild.
  //
  // Used to also offer a "Cerca paziente" mode here, removed - Giorno/Settimana are
  // for a therapist to check their own agenda, and patient lookup didn't fit that.
  // See Patients/PlanData + the "Genera piano" button on the patient form instead,
  // for looking up a specific patient's full schedule.
  function buildEntitySelector(state, onChange) {
    var $wrap = $('<span class="entity-selector"></span>');

    var $select = $('<select class="scheduling-therapist-select"></select>');
    $select.append('<option value="">-- Seleziona terapista --</option>');
    $select.append('<option value="reparto-0">Reparto Uomini</option>');
    $select.append('<option value="reparto-1">Reparto Donne</option>');
    schedulingTherapists.forEach(function(t) {
      $select.append('<option value="' + t.id + '">' + t.name + '</option>');
    });

    var currentSelectValue = '';
    if (state.mode === 'therapist') {
      currentSelectValue = state.therapistId || '';
    } else if (state.mode === 'reparto') {
      currentSelectValue = 'reparto-' + state.repartoSex;
    }
    $select.val(currentSelectValue);

    $select.on('change', function() {
      var val = $select.val();
      if (val === 'reparto-0' || val === 'reparto-1') {
        state.mode = 'reparto';
        state.repartoSex = val === 'reparto-1' ? 1 : 0;
        state.therapistId = null;
      } else if (val) {
        state.mode = 'therapist';
        state.therapistId = parseInt(val, 10);
        state.repartoSex = null;
      } else {
        state.mode = null;
        state.therapistId = null;
        state.repartoSex = null;
      }
      onChange();
    });

    $wrap.append($select);
    return $wrap;
  }

  function buildGiornoHeader() {
    var $header = $('<div class="scheduling-header"></div>');

    var state = {
      mode: giornoMode,
      therapistId: giornoSelectedTherapistId,
      repartoSex: giornoSelectedRepartoSex
    };

    var $selector = buildEntitySelector(state, function() {
      giornoMode = state.mode;
      giornoSelectedTherapistId = state.therapistId;
      giornoSelectedRepartoSex = state.repartoSex;
      buildGiornoView();
    });

    var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
    $prev.on('click', function() {
      giornoCurrentDate = addWeekdays(giornoCurrentDate, -1);
      buildGiornoView();
    });

    var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
    $next.on('click', function() {
      giornoCurrentDate = addWeekdays(giornoCurrentDate, 1);
      buildGiornoView();
    });

    var $dateLabel = $('<span class="scheduling-date-label scheduling-date-label-clickable"></span>').text(formatGiornoLabel(giornoCurrentDate));
    $dateLabel.on('click', function() {
      showQuickDatePicker(giornoCurrentDate, function(pickedDate) {
        giornoCurrentDate = pickedDate;
        buildGiornoView();
      });
    });

    var $today = $('<button type="button" class="secondary">Vai ad Oggi</button>');
    $today.on('click', function() {
      giornoCurrentDate = getTodayForGiorno();
      buildGiornoView();
    });

    $header
      .append($selector)
      .append($prev).append($dateLabel).append($next).append($today);
    return $header;
  }

  // Same as buildGiornoHeader, but steps by whole weeks (Mon-Fri) instead of single
  // weekdays, and "Vai ad Oggi" jumps to the current week's Monday.
  function buildSettimanaHeader() {
    var $header = $('<div class="scheduling-header"></div>');

    var state = {
      mode: settimanaMode,
      therapistId: settimanaSelectedTherapistId,
      repartoSex: settimanaSelectedRepartoSex
    };

    var $selector = buildEntitySelector(state, function() {
      settimanaMode = state.mode;
      settimanaSelectedTherapistId = state.therapistId;
      settimanaSelectedRepartoSex = state.repartoSex;
      buildSettimanaView();
    });

    var weekEnd = addDays(settimanaCurrentWeekStart, 4);

    var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
    $prev.on('click', function() {
      settimanaCurrentWeekStart = addDays(settimanaCurrentWeekStart, -7);
      buildSettimanaView();
    });

    var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
    $next.on('click', function() {
      settimanaCurrentWeekStart = addDays(settimanaCurrentWeekStart, 7);
      buildSettimanaView();
    });

    var $dateLabel = $('<span class="scheduling-date-label scheduling-date-label-clickable"></span>').text(formatSettimanaLabel(settimanaCurrentWeekStart, weekEnd));
    $dateLabel.on('click', function() {
      showQuickDatePicker(settimanaCurrentWeekStart, function(pickedDate) {
        settimanaCurrentWeekStart = getMondayOfWeek(pickedDate);
        buildSettimanaView();
      });
    });

    var $today = $('<button type="button" class="secondary">Vai ad Oggi</button>');
    $today.on('click', function() {
      settimanaCurrentWeekStart = getMondayOfWeek(new Date());
      buildSettimanaView();
    });

    $header
      .append($selector)
      .append($prev).append($dateLabel).append($next).append($today);

    if (ripianificaDraft) {
      var $confirmBtn = $('<button type="button">Conferma spostamento</button>');
      $confirmBtn.on('click', function() {
        $.ajax({
          url: 'Giorno/Slot/' + ripianificaDraft.originalSlotId + '/RipianificaConfirm',
          method: 'POST',
          contentType: 'application/json',
          data: JSON.stringify({ date: ripianificaDraft.date, timeSlot: ripianificaDraft.timeSlot })
        }).done(function() {
          ripianificaDraft = null;
          if (ripianificaReturnView) {
            var returnView = ripianificaReturnView;
            ripianificaReturnView = null;
            navigateTo(returnView);
          } else {
            buildSettimanaView();
          }
        }).fail(function(jqXHR) {
          var msg = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Spostamento non riuscito.';
          window.showModal('<p>' + msg + '</p>', [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
        });
      });

      var $abortBtn = $('<button type="button" class="danger">Annulla spostamento</button>');
      $abortBtn.on('click', function() {
        ripianificaDraft = null;
        if (ripianificaReturnView) {
          var returnView = ripianificaReturnView;
          ripianificaReturnView = null;
          navigateTo(returnView);
        } else {
          buildSettimanaView();
        }
      });

      $header.append($confirmBtn).append($abortBtn);
    }

    return $header;
  }

  // Backs the existingCount badge in the shared Giorno/Settimana grid below - fetches
  // session details for one exact date+timeslot on demand (Giorno/SlotDetails)
  // rather than sending them with the grid data, to keep that response lightweight.
  // Deliberately a separate top-level function rather than reusing the nested
  // showRepartoSlotPopup/showMixedSlotPopup (each scoped inside its own Pianificazione
  // view) - same modal look, but self-contained here to avoid touching those.
  function showGiornoSettimanaSlotPopup(dateObj, timeSlot) {
    $.get('Giorno/SlotDetails', { date: formatDateISO(dateObj), timeSlot: timeSlot }).done(function(sessions) {
      var title = 'Terapie di ' + formatGiornoLabel(dateObj);

      var itemsHtml = sessions.map(function(s) {
        var therapistLabel = s.therapistName ? s.therapistName : 'Reparto';
        var therapyColor = s.therapyTypeColor != null ? intToHexColor(s.therapyTypeColor) : '#000';
        return '<li class="slot-details-line" data-slot-id="' + s.id + '">' + s.patientName + ' - <b>' + therapistLabel + '</b> - ' +
          '<span style="color:' + therapyColor + '">' + s.therapyTypeName + '</span></li>';
      }).join('');

      window.showModal(
        '<h3>' + title + '</h3><ul>' + itemsHtml + '</ul>',
        [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]
      );

      $('#modal-body .slot-details-line').on('click', function() {
        showSlotDetailPopup($(this).data('slot-id'));
      });
    });
  }

  // Refreshes whichever of Giorno/Settimana is currently on screen - called after a
  // slot detail popup action (change therapist, delete, change status) succeeds, so
  // the grid reflects it immediately.
  function refreshGiornoSettimanaView() {
    if (currentView === 'giorno') {
      renderGiornoView();
    } else if (currentView === 'settimana') {
      renderSettimanaView();
    }
  }

  // Opened by clicking a slot directly in Giorno/Settimana, or a line in the
  // existingCount badge popup above. Shows therapy/date/time/patient/therapist, plus
  // role-appropriate actions (see Giorno/Slot/{id} on the server for the permission
  // rules - Admin can act on any slot, a plain Therapist only on one assigned to them).
  function showSlotDetailPopup(slotId) {
    $.get('Giorno/Slot/' + slotId).done(function(detail) {
      var isAdmin = currentSession && currentSession.isAccettazione;
      var isAssignedTherapist = !isAdmin && currentSession && detail.therapistId === currentSession.id;

      var bodyHtml = buildSlotDetailBandHtml(detail);

      if (isAdmin || isAssignedTherapist) {
        bodyHtml += buildStatusRowHtml(detail, isAdmin);
      }

      var actions = [];

      if (isAdmin) {
        actions.push({
          label: 'Cambia terapista',
          className: 'secondary',
          onClick: function() { showChangeTherapistPopup(detail); }
        });

        actions.push({
          label: 'Sposta a fine terapia',
          className: 'secondary',
          onClick: function() {
            $.get('Giorno/Slot/' + detail.id + '/RipianificaPropose')
              .done(function(proposal) {
                ripianificaDraft = {
                  originalSlotId: proposal.originalSlotId,
                  therapyPartId: proposal.therapyPartId,
                  therapistId: proposal.therapistId,
                  therapistName: proposal.therapistName,
                  patientName: proposal.patientName,
                  therapyTypeName: proposal.therapyTypeName,
                  therapyTypeColor: proposal.therapyTypeColor,
                  durationSlots: proposal.durationSlots,
                  date: proposal.proposedDate,
                  timeSlot: proposal.proposedTimeSlot
                };
                settimanaMode = 'therapist';
                settimanaSelectedTherapistId = proposal.therapistId;
                settimanaCurrentWeekStart = getMondayOfWeek(parseDateOnly(proposal.proposedDate));
                navigateTo('settimana');
              })
              .fail(function(jqXHR) {
                var msg = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Impossibile calcolare una proposta.';
                window.showModal('<p>' + msg + '</p>', [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
              });
          }
        });

        actions.push({
          label: 'Elimina',
          className: 'danger',
          onClick: function() { showSlotDeleteConfirmPopup(detail); }
        });

        if (detail.therapyId) {
          actions.push({
            label: 'Cancella tutte le sedute future',
            className: 'danger',
            onClick: function() { showCancelFutureSlotsModal(detail.therapyId, detail.patientName, detail.date); }
          });
        }
      }

      actions.push({ label: 'Chiudi', className: 'secondary', onClick: function() {} });

      window.showModal(bodyHtml, actions);

      if (isAdmin || isAssignedTherapist) {
        $('#modal-body #slot-status-select').on('change', function() {
          changeSlotStatus(detail.id, parseInt($(this).val(), 10));
        });
      }
    });
  }

  // Therapy band (colored from TherapyType.Color) + date/time + patient (bold) +
  // therapist/Reparto - shared between the slot detail popup and the "Cambia
  // terapista" sub-popup, so the latter always shows what's being reassigned.
  function buildSlotDetailBandHtml(detail) {
    var therapyColor = detail.therapyTypeColor != null ? intToHexColor(detail.therapyTypeColor) : '#888';
    var therapistLabel = detail.therapistName ? detail.therapistName : 'Reparto';
    var dateObj = parseDateISO(detail.date);

    return '<div class="slot-detail-band" style="background:' + therapyColor + '">' +
        '<span class="slot-detail-therapy-name">' + detail.therapyTypeName + '</span>' +
      '</div>' +
      '<div class="slot-detail-datetime">' + formatGiornoLabel(dateObj) + ' - ' + slotToTime(detail.timeSlot) + '</div>' +
      '<div class="slot-detail-patient"><b>' + detail.patientName + '</b></div>' +
      '<div class="slot-detail-therapist">' + therapistLabel + '</div>';
  }

  var slotStatusLabels = { 0: 'Da fare', 1: 'Fatto', 2: 'Assente' };

  // "Stato" line with a dropdown of the statuses the current user may set (see
  // Giorno/Slot/{id}/ChangeStatus for the matching server-side rule) - the slot's
  // current status is always included even if it's outside that set, so the
  // dropdown never misrepresents what's actually stored.
  function buildStatusRowHtml(detail, isAdmin) {
    var allowedStatuses = isAdmin ? [0, 1, 2] : [1, 2];

    if (allowedStatuses.indexOf(detail.status) === -1) {
      allowedStatuses = [detail.status].concat(allowedStatuses);
    }

    var optionsHtml = allowedStatuses.map(function(v) {
      var selected = v === detail.status ? ' selected' : '';
      return '<option value="' + v + '"' + selected + '>' + slotStatusLabels[v] + '</option>';
    }).join('');

    return '<div class="slot-detail-status-row">' +
      '<label for="slot-status-select">Stato</label>' +
      '<select id="slot-status-select">' + optionsHtml + '</select>' +
    '</div>';
  }

  function changeSlotStatus(slotId, status) {
    $.ajax({
      url: 'Giorno/Slot/' + slotId + '/ChangeStatus',
      method: 'POST',
      contentType: 'application/json',
      data: JSON.stringify({ status: status })
    }).done(function() {
      $('#modal-overlay').hide();
      refreshGiornoSettimanaView();
    });
  }

  // Simple Sì/No confirmation, no confirm-name typing - CPU wants this action
  // lightweight, unlike the other Remove flows elsewhere in the app.
  function showSlotDeleteConfirmPopup(detail) {
    var therapistLabel = detail.therapistName ? detail.therapistName : 'Reparto';
    var dateObj = parseDateISO(detail.date);
    var message = 'Sei sicuro di rimuovere la terapia di ' + detail.patientName + ' con ' + therapistLabel +
      ' per il ' + formatGiornoLabel(dateObj) + ' alle ' + slotToTime(detail.timeSlot) + '?';

    window.showModal('<p>' + message + '</p>', [
      {
        label: 'Sì',
        className: 'danger',
        onClick: function() {
          $.ajax({
            url: 'Giorno/Slot/Remove/' + detail.id,
            method: 'POST'
          }).done(function() {
            refreshGiornoSettimanaView();
          });
        }
      },
      { label: 'No', className: 'secondary', onClick: function() {} }
    ]);
  }

  // Sub-popup for "Cambia terapista" - shows the same therapy details as the detail
  // popup, then the therapist pool appropriate to the slot's category (Palestra vs
  // Reparto), marking each as available/busy for that exact date+timeslot
  // (Giorno/Slot/{id}/AvailableTherapists already excludes the conflict
  // false-positive against the slot's own current therapist).
  function showChangeTherapistPopup(detail) {
    $.get('Giorno/Slot/' + detail.id + '/AvailableTherapists').done(function(therapists) {
      var itemsHtml = therapists.map(function(t) {
        var disabledClass = t.available ? '' : ' change-therapist-unavailable';
        var currentTag = t.isCurrent ? ' (attuale)' : '';
        return '<li class="change-therapist-line' + disabledClass + '" data-therapist-id="' + t.id + '" data-available="' + t.available + '">' +
          t.name + currentTag + '</li>';
      }).join('');

      var bodyHtml = '<h3>Scegli un nuovo terapista per l\'attività</h3>' +
        buildSlotDetailBandHtml(detail) +
        '<ul class="change-therapist-list">' + itemsHtml + '</ul>';

      window.showModal(
        bodyHtml,
        [{ label: 'Annulla', className: 'secondary', onClick: function() { showSlotDetailPopup(detail.id); } }]
      );

      $('#modal-body .change-therapist-line[data-available="true"]').on('click', function() {
        var therapistId = $(this).data('therapist-id');

        $.ajax({
          url: 'Giorno/Slot/' + detail.id + '/ChangeTherapist',
          method: 'POST',
          contentType: 'application/json',
          data: JSON.stringify({ therapistId: therapistId })
        }).done(function() {
          refreshGiornoSettimanaView();
        });
      });
    });
  }

  // Debug info for the Reparto capacity number on an empty cell - Admin only, Giorno
  // and Settimana both (same shared grid). CPU: "clicking a cell in week view should
  // give us all info to debug." Shows exactly which relevant therapists are/aren't
  // counted at that date+timeslot, and why (Giorno/SlotCapacityDebug mirrors
  // ComputeRepartoCapacityAndDemand's own checks, one by one).
  function showCapacityDebugPopup(dateObj, timeSlot) {
    $.get('Giorno/SlotCapacityDebug', { date: formatDateISO(dateObj), timeSlot: timeSlot }).done(function(info) {
      var title = 'Debug capacità - ' + formatGiornoLabel(dateObj) + ' ' + slotToTime(timeSlot);

      var rowsHtml = info.therapists.map(function(t) {
        var statusClass = t.counted ? 'capacity-debug-counted' : 'capacity-debug-excluded';
        var kindLabel = t.isRepartoCapable ? 'Reparto' : 'Covering';
        return '<tr class="' + statusClass + '"><td>' + t.name + '</td><td>' + kindLabel + '</td><td>' + t.reason + '</td></tr>';
      }).join('');

      var bodyHtml =
        '<h3>' + title + '</h3>' +
        '<p>Capacità: <b>' + info.capacity + '</b> &nbsp; Domanda (Light): <b>' + info.demand + '</b> &nbsp; ' +
        'Sessioni Reparto totali: <b>' + info.existingCount + '</b></p>' +
        '<p class="capacity-debug-rates">Tariffa Reparto: ' + info.repartoRate + ' &nbsp; Tariffa Covering: ' + info.coveringRate + '</p>' +
        '<table class="capacity-debug-table"><thead><tr><th>Terapista</th><th>Tipo</th><th>Stato</th></tr></thead>' +
        '<tbody>' + rowsHtml + '</tbody></table>';

      window.showModal(bodyHtml, [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
    });
  }

  // Shared N-column schedule grid, used by both Giorno (1 column) and Settimana (5
  // columns, Mon-Fri) - same rendering either way, only the column list differs.
  // Every column gets: the always-visible Reparto capacity background + count badge,
  // an availability/vacation overlay (Therapist mode: pink band outside that day's
  // own TherapistAvailability ranges, red-ish vacation band on top of that; Patient/
  // Reparto mode: vacation band only, "available" everywhere else), and real booked
  // slots drawn as absolutely-positioned overlay boxes with side-by-side overlap
  // splitting. Deliberately no rowspan merging for the overlay band - every 15-minute
  // row stays its own TD (see the old Pianificazione drop-position bug this avoids).
  //
  // Each entry in `columns` is:
  //   { headerLabel, date, slots, capacityGrid, availability (optional), vacationMatch }
  // `availability` is the list of { startTime, endTime } ranges that are NOT pink for
  // this column - omit it (Reparto/Patient modes) to make the whole [gridStart,gridEnd)
  // range "available" and let only vacationMatch produce an overlay.
  // rowHeightPx is optional - defaults to the normal fixed on-screen row height.
  // Only the Giorno print path currently overrides it (see computeStampaGiornoSizing),
  // to shrink/grow rows so a whole day always fits on one A4 page regardless of how
  // many 15-minute slots it has.
  function buildScheduleGridTable(columns, gridStart, gridEnd, capacityWarningThreshold, labelFn, rowHeightPx) {
    rowHeightPx = rowHeightPx || PIANIFICAZIONE_ROW_HEIGHT;
    var rowCount = gridEnd - gridStart;

    var $wrapper = $('<div class="scheduling-view pianificazione-wrapper"></div>');
    var $table = $('<table class="scheduling-grid pianificazione-grid"></table>');

    var headerHtml = '<tr><th>Orario</th>';
    columns.forEach(function(col) {
      headerHtml += '<th>' + col.headerLabel + '</th>';
    });
    headerHtml += '</tr>';
    $table.append('<thead>' + headerHtml + '</thead>');

    var colData = columns.map(function(col) {
      var capIndex = {};
      (col.capacityGrid || []).forEach(function(c) { capIndex[c.timeSlot] = c; });

      var availability = col.availability || [{ startTime: gridStart, endTime: gridEnd }];
      var overlaySlots = computeColumnSlots(availability, col.vacationMatch, gridStart, gridEnd);

      var items = col.slots.map(function(s) {
        return {
          key: 'slot-' + s.id,
          id: s.id,
          startSlot: s.timeSlot,
          endSlot: s.timeSlot + s.durationSlots,
          label: labelFn(s, col.date),
          isReparto: s.therapyTypeCategory === 0,
          color: s.therapyTypeColor
        };
      });

      if (currentView === 'settimana' && ripianificaDraft && formatDateISO(col.date) === ripianificaDraft.date) {
        items.push({
          key: 'ripianifica-draft',
          isDraft: true,
          startSlot: ripianificaDraft.timeSlot,
          endSlot: ripianificaDraft.timeSlot + ripianificaDraft.durationSlots,
          label: ripianificaDraft.patientName + ' - ' + ripianificaDraft.therapyTypeName + ' (proposta)'
        });
      }

      return { overlaySlots: overlaySlots, capIndex: capIndex, items: items, groups: computeOverlapGroups(items), tdRefs: [] };
    });

    var $tbody = $('<tbody></tbody>');

    for (var row = 0; row < rowCount; row++) {
      var slotVal = gridStart + row;
      var $row = $('<tr></tr>');
      $row.append('<td class="scheduling-time-cell" style="height:' + rowHeightPx + 'px">' + slotToTime(slotVal) + '</td>');

      columns.forEach(function(col, c) {
        var cd = colData[c];
        var overlayInfo = cd.overlaySlots[row];
        var prevInfo = row > 0 ? cd.overlaySlots[row - 1] : null;
        var isRunStart = !prevInfo || prevInfo.type !== overlayInfo.type || prevInfo.label !== overlayInfo.label;

        var capEntry = cd.capIndex[slotVal];
        var capacity = capEntry ? capEntry.capacity : 0;
        var demand = capEntry ? capEntry.demand : 0;
        var existingCount = capEntry ? capEntry.existingCount : 0;
        var capacityClass = giornoCapacityColorClass(capacity, demand, capacityWarningThreshold);

        var overlayClass = overlayInfo.type === 'vacation' ? ' vacation-overlay' :
          (overlayInfo.type === 'unavailable' ? ' availability-overlay' : '');

        var $td = $('<td class="giorno-settimana-cell ' + capacityClass + overlayClass + '" style="height:' + rowHeightPx + 'px"></td>');

        if (overlayInfo.type === 'vacation' && isRunStart) {
          $td.append('<span class="giorno-vacation-label">' + overlayInfo.label + '</span>');
        }

        if (existingCount > 0) {
          var $count = $('<span class="pianificazione-reparto-count">' + existingCount + '</span>');
          (function(colDate, clickSlot) {
            $count.on('click', function(e) {
              e.stopPropagation();
              showGiornoSettimanaSlotPopup(colDate, clickSlot);
            });
          })(col.date, slotVal);
          $td.append($count);
        }

        if (currentSession && currentSession.isAccettazione) {
          (function(colDate, clickSlot) {
            $td.on('click', function() {
              if (showCapacityDebug) {
                showCapacityDebugPopup(colDate, clickSlot);
              }
            });
          })(col.date, slotVal);

          $td.on('dragover', function(e) {
            e.preventDefault();

            if (!$giornoSettimanaDragOverCell || $giornoSettimanaDragOverCell[0] !== this) {
              clearGiornoSettimanaDragOverHighlight();
              $giornoSettimanaDragOverCell = $(this);
              $giornoSettimanaDragOverCell.addClass('giorno-settimana-cell-dragover');
            }
          });

          $td.on('dragleave', function() {
            if ($giornoSettimanaDragOverCell && $giornoSettimanaDragOverCell[0] === this) {
              clearGiornoSettimanaDragOverHighlight();
            }
          });

          (function(colDate, targetSlot) {
            $td.on('drop', function(e) {
              e.preventDefault();
              clearGiornoSettimanaDragOverHighlight();

              if (draggedRipianificaDraft) {
                draggedRipianificaDraft = false;
                ripianificaDraft.date = formatDateISO(colDate);
                ripianificaDraft.timeSlot = targetSlot;
                buildSettimanaView();
                return;
              }

              if (draggedGiornoSettimanaSlotId == null) {
                return;
              }

              var draggedId = draggedGiornoSettimanaSlotId;
              var $draggedOverlay = draggedGiornoSettimanaOverlay;

              // Instant snap to the target cell (no transition) - purely a visual
              // preview of the drop; the real layout only comes from the server-driven
              // redraw on success (see refreshGiornoSettimanaView).
              if ($draggedOverlay) {
                var fromRect = $draggedOverlay[0].getBoundingClientRect();
                var toRect = this.getBoundingClientRect();
                $draggedOverlay.css({
                  transition: 'none',
                  transform: 'translate(' + (toRect.left - fromRect.left) + 'px, ' + (toRect.top - fromRect.top) + 'px)',
                  zIndex: 10
                });
              }

              $.ajax({
                url: 'Giorno/Slot/' + draggedId + '/Move',
                method: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ date: formatDateISO(colDate), timeSlot: targetSlot })
              }).done(function() {
                refreshGiornoSettimanaView();
              }).fail(function() {
                // If the overlay is still attached, we're on the same week it started
                // in - slide it back over half a second. If not (an edge-drag navigated
                // to a different week mid-drag, replacing the whole grid), there's
                // nothing valid to animate - just recharge instead (CPU's call).
                if ($draggedOverlay && document.body.contains($draggedOverlay[0])) {
                  requestAnimationFrame(function() {
                    $draggedOverlay.css('transition', 'transform 0.5s ease');
                    $draggedOverlay.css('transform', 'translate(0, 0)');
                  });

                  setTimeout(function() {
                    $draggedOverlay.css({ transition: '', transform: '', zIndex: '' });
                  }, 550);
                } else {
                  refreshGiornoSettimanaView();
                }
              });
            });
          })(col.date, slotVal);
        }

        $row.append($td);
        cd.tdRefs[row] = $td;
      });

      $tbody.append($row);
    }

    $table.append($tbody);

    columns.forEach(function(col, c) {
      var cd = colData[c];
      cd.items.forEach(function(item) {
        var rowIndex = item.startSlot - gridStart;
        if (rowIndex < 0 || rowIndex >= rowCount) {
          return;
        }

        var span = item.endSlot - item.startSlot;
        var groupInfo = cd.groups[item.key];
        var groupSize = groupInfo.groupSize;
        var idx = groupInfo.indexInGroup;

        var overlayClass = item.isDraft
          ? 'pianificazione-session-overlay pianificazione-reparto-overlay pianificazione-draft pianificazione-ripianifica-draft'
          : 'pianificazione-session-overlay pianificazione-reparto-overlay pianificazione-occupied-same';

        var $overlay = $('<div class="' + overlayClass + '" title="' + item.label + '"><span class="pianificazione-cell-label">' + item.label + '</span></div>');
        $overlay.css({
          top: '0',
          height: (span * rowHeightPx - 1) + 'px',
          left: 'calc((100% - 2em) * ' + idx + ' / ' + groupSize + ')',
          width: 'calc((100% - 2em) / ' + groupSize + ')'
        });

        if (item.isReparto) {
          var hex = intToHexColor(item.color);
          $overlay.css({
            background: hex,
            border: '1px solid ' + halfBrightnessHex(hex)
          });
        }

        var $startTd = cd.tdRefs[rowIndex];
        if ($startTd) {
          $startTd.append($overlay);
        }

        if (item.isDraft) {
          $overlay.attr('draggable', 'true');

          $overlay.on('dragstart', function(e) {
            draggedRipianificaDraft = true;
            if (e.originalEvent && e.originalEvent.dataTransfer) {
              e.originalEvent.dataTransfer.effectAllowed = 'move';
            }
          });

          $overlay.on('dragend', function() {
            draggedRipianificaDraft = false;
            clearGiornoSettimanaDragOverHighlight();
          });

          return;
        }

        $overlay.on('click', function(e) {
          e.stopPropagation();
          showSlotDetailPopup(item.id);
        });

        if (currentSession && currentSession.isAccettazione) {
          $overlay.attr('draggable', 'true');

          $overlay.on('dragstart', function(e) {
            draggedGiornoSettimanaSlotId = item.id;
            draggedGiornoSettimanaOverlay = $overlay;
            if (e.originalEvent && e.originalEvent.dataTransfer) {
              e.originalEvent.dataTransfer.effectAllowed = 'move';
            }
          });

          $overlay.on('dragend', function() {
            draggedGiornoSettimanaSlotId = null;
            draggedGiornoSettimanaOverlay = null;
            clearGiornoSettimanaDragOverHighlight();
          });
        }
      });
    });

    $wrapper.append($table);
    return $wrapper;
  }

  // Single-column day grid - thin wrapper around the shared N-column renderer.
  // Returns the built element rather than writing to #content itself, so callers
  // (the normal Giorno view, or the print preview which prepends its own header)
  // can decide what else goes alongside it. rowHeightPx is optional, see
  // buildScheduleGridTable - only the Giorno print path currently overrides it.
  function buildGiornoGridTable(data, vacationMatch, labelFn, rowHeightPx) {
    return buildScheduleGridTable(
      [{
        headerLabel: data.name,
        date: giornoCurrentDate,
        slots: data.slots,
        capacityGrid: data.capacityGrid,
        availability: data.availability,
        vacationMatch: vacationMatch
      }],
      data.gridStart, data.gridEnd, data.capacityWarningThreshold, labelFn, rowHeightPx
    );
  }

  // Five-column (Mon-Fri) week grid - thin wrapper around the shared N-column
  // renderer. `vacationMatchFn(colDate)` lets each mode source its vacation-matching
  // differently (Therapist: own + global; Reparto/Patient: global only), same as Giorno.
  // rowHeightPx is optional, same reasoning as buildGiornoGridTable above - only the
  // Settimana print path overrides it (see computeStampaSettimanaSizing).
  function buildSettimanaGridTable(data, vacationMatchFn, labelFn, rowHeightPx) {
    var columns = data.days.map(function(day) {
      var colDate = parseDateISO(day.date);
      return {
        headerLabel: weekDays[colDate.getDay() - 1].label + ' ' + formatDateDDMM(colDate),
        date: colDate,
        slots: day.slots,
        capacityGrid: day.capacityGrid,
        availability: day.availability,
        vacationMatch: vacationMatchFn(colDate)
      };
    });

    return buildScheduleGridTable(columns, data.gridStart, data.gridEnd, data.capacityWarningThreshold, labelFn, rowHeightPx);
  }

  // Per-mode session labels, shared by Giorno and Settimana alike.
  function therapistModeLabel(slotInfo) {
    return slotInfo.patientName + ' - ' + slotInfo.therapyTypeName;
  }

  function repartoModeLabel(slotInfo) {
    return slotInfo.therapistId
      ? (slotInfo.patientName + ' - ' + slotInfo.therapistName + ' - ' + slotInfo.therapyTypeName)
      : (slotInfo.patientName + ' - ' + slotInfo.therapyTypeName);
  }

  function buildGiornoView() {
    var $header = buildGiornoHeader();
    setTopbarActions($header);

    abortIfActive(giornoActiveRequest);

    if (!giornoMode) {
      $('#content').empty().append('<div class="scheduling-empty">Seleziona un terapista per visualizzare il calendario.</div>');
      return;
    }

    if (giornoMode === 'therapist') {
      giornoActiveRequest = $.get('Giorno/TherapistData', { therapistId: giornoSelectedTherapistId, date: formatDateISO(giornoCurrentDate) })
        .done(function(data) {
          if (!data.hasAvailability) {
            $('#content').empty().append('<div class="scheduling-empty">Nessuna disponibilità per il terapista ' + data.name + '</div>');
            return;
          }

          var vacationMatch = findVacationForDate(data.vacations, giornoCurrentDate, data.name);
          var $wrapper = buildGiornoGridTable(data, vacationMatch, therapistModeLabel);
          $('#content').empty().append($wrapper);
        });
      return;
    }

    giornoActiveRequest = $.get('Giorno/RepartoData', { sex: giornoSelectedRepartoSex, date: formatDateISO(giornoCurrentDate) })
      .done(function(data) {
        // Vacations here are already global-only (server-side filtered), so this
        // covers the whole column same as therapist mode - no per-slot flagging
        // needed since individual therapist availability already shaped the
        // background capacity/demand values themselves.
        var vacationMatch = findVacationForDate(data.vacations, giornoCurrentDate, '');
        var $wrapper = buildGiornoGridTable(data, vacationMatch, repartoModeLabel);
        $('#content').empty().append($wrapper);
      });
  }

  function renderSettimanaView() {
    if (!settimanaCurrentWeekStart) {
      settimanaCurrentWeekStart = getMondayOfWeek(new Date());
    }

    loadSchedulingTherapists(function() {
      buildSettimanaView();
    });
  }

  // Week (Mon-Fri) version of Giorno - same modes, same shared-axis grid rendering
  // (buildScheduleGridTable), just backed by the Settimana/* endpoints instead of
  // Giorno/* and stepping by whole weeks. See buildGiornoView for the single-day
  // counterpart this mirrors.
  function buildSettimanaView() {
    var $header = buildSettimanaHeader();
    setTopbarActions($header);

    if (!settimanaMode) {
      $('#content').empty().append('<div class="scheduling-empty">Seleziona un terapista per visualizzare il calendario.</div>');
      return;
    }

    // Edge-drag week navigation (drag a slot near the grid's left/right edge to flip
    // week, same 2.5s-hold mechanism as Pianificazione) - Admin only, matching the
    // slot drag-and-drop itself being Admin-only. Giorno has no day-navigation
    // equivalent by design (CPU: "if in Giorno, you cannot change the day").
    if (currentSession && currentSession.isAccettazione) {
      setupEdgeOverlays(function(side) {
        settimanaCurrentWeekStart = addDays(settimanaCurrentWeekStart, side === 'left' ? -7 : 7);
        buildSettimanaView();
      });
    }

    var weekStartISO = formatDateISO(settimanaCurrentWeekStart);

    abortIfActive(settimanaActiveRequest);

    if (settimanaMode === 'therapist') {
      settimanaActiveRequest = $.get('Settimana/TherapistData', { therapistId: settimanaSelectedTherapistId, weekStart: weekStartISO })
        .done(function(data) {
          if (!data.hasAvailability) {
            $('#content').empty().append('<div class="scheduling-empty">Nessuna disponibilità per il terapista ' + data.name + ' questa settimana.</div>');
            return;
          }

          var $wrapper = buildSettimanaGridTable(data, function(colDate) {
            return findVacationForDate(data.vacations, colDate, data.name);
          }, therapistModeLabel);
          $('#content').empty().append($wrapper);
          positionEdgeOverlays($wrapper.find('table'));
        });
      return;
    }

    settimanaActiveRequest = $.get('Settimana/RepartoData', { sex: settimanaSelectedRepartoSex, weekStart: weekStartISO })
      .done(function(data) {
        var $wrapper = buildSettimanaGridTable(data, function(colDate) {
          return findVacationForDate(data.vacations, colDate, '');
        }, repartoModeLabel);
        $('#content').empty().append($wrapper);
        positionEdgeOverlays($wrapper.find('table'));
      });
  }

  // --- Stampa (print preview for Giorno/Settimana) ----------------------------
  //
  // Reached only via the print button shown on Giorno/Settimana (see loadView),
  // which stashes which of the two we came from in printSourceView first. Reuses
  // the exact same fetch + grid-building code as the normal views - the only
  // difference is a small header (entity name + date/week) prepended above the
  // grid, and Stampa/Chiudi buttons in place of the normal mode selector/nav
  // header. app.css hides the topbar/sidebar and strips all color when actually
  // printing (@media print), so only this header + grid end up on paper.

  // A4 portrait usable height after margins, in cm - STAMPA_PAGE_MARGIN_CM mirrors
  // what app.css's @media print compensates for (@page margin:0 + .content padding).
  // A day/week's row count varies a lot (a therapist's full availability window vs.
  // a patient's AM-only sessions vs. Reparto's whole clinic day, or 5 days' worth of
  // the same for Settimana), so a fixed font/row size can't "fit one page" in
  // general - this computes a row height (and matching font size) from the actual
  // number of rows instead, every time, clamped to sane min/max so it never gets
  // illegibly thin or absurdly huge. Same formula for both Giorno (portrait) and
  // Settimana (landscape) - only the page height differs, since orientation swaps
  // which A4 dimension is "height".
  // These are starting values - may need a tweak after a real print test.
  var STAMPA_PAGE_MARGIN_CM = 1.2;
  var STAMPA_PAGE_HEIGHT_CM = 29.7 - (2 * STAMPA_PAGE_MARGIN_CM);
  var STAMPA_LANDSCAPE_PAGE_HEIGHT_CM = 21 - (2 * STAMPA_PAGE_MARGIN_CM);
  var STAMPA_HEADER_RESERVED_CM = 2.2;       // entity name + date + breathing room
  var STAMPA_TABLE_HEADER_RESERVED_CM = 0.9; // the grid's own <thead> row
  var STAMPA_ROW_HEIGHT_MIN_CM = 0.45;
  var STAMPA_ROW_HEIGHT_MAX_CM = 1.3;
  var STAMPA_CM_TO_PX = 37.795; // ~96dpi / 2.54cm - only used so the on-screen preview matches print sizing

  function computeStampaGiornoSizing(rowCount) {
    var usableHeightCm = STAMPA_PAGE_HEIGHT_CM - STAMPA_HEADER_RESERVED_CM - STAMPA_TABLE_HEADER_RESERVED_CM;
    var rowHeightCm = usableHeightCm / Math.max(rowCount, 1);
    rowHeightCm = Math.max(STAMPA_ROW_HEIGHT_MIN_CM, Math.min(STAMPA_ROW_HEIGHT_MAX_CM, rowHeightCm));

    var fontSizeCm = rowHeightCm * 0.62;
    fontSizeCm = Math.max(0.25, Math.min(0.85, fontSizeCm));

    return {
      rowHeightPx: Math.round(rowHeightCm * STAMPA_CM_TO_PX),
      fontSizePx: Math.round(fontSizeCm * STAMPA_CM_TO_PX)
    };
  }

  // Same as computeStampaGiornoSizing above, just off the landscape page height -
  // Settimana's 5-column header is still a single text line per column same as
  // Giorno's, so the other reserved-space constants are shared as-is.
  function computeStampaSettimanaSizing(rowCount) {
    var usableHeightCm = STAMPA_LANDSCAPE_PAGE_HEIGHT_CM - STAMPA_HEADER_RESERVED_CM - STAMPA_TABLE_HEADER_RESERVED_CM;
    var rowHeightCm = usableHeightCm / Math.max(rowCount, 1);
    rowHeightCm = Math.max(STAMPA_ROW_HEIGHT_MIN_CM, Math.min(STAMPA_ROW_HEIGHT_MAX_CM, rowHeightCm));

    var fontSizeCm = rowHeightCm * 0.62;
    fontSizeCm = Math.max(0.25, Math.min(0.85, fontSizeCm));

    return {
      rowHeightPx: Math.round(rowHeightCm * STAMPA_CM_TO_PX),
      fontSizePx: Math.round(fontSizeCm * STAMPA_CM_TO_PX)
    };
  }

  // @page rules can't be scoped by a CSS class - they always apply to the whole print
  // job - so orientation is swapped via an injected <style> tag each time the print
  // preview loads, depending on which view (Giorno=portrait, Settimana=landscape) it
  // came from. Removed again in loadView whenever navigating away from Stampa.
  function setStampaPageOrientation(orientation) {
    $('#stampa-page-style').remove();
    $('<style id="stampa-page-style">@page { size: ' + orientation + '; }</style>').appendTo('head');
  }

  // Scales a print grid's font sizes to match computeStampaGiornoSizing, via a scoped
  // <style> tag rather than touching dozens of individual elements' inline styles -
  // $grid just gets a class to hang these rules off. Removed/replaced the same way
  // as setStampaPageOrientation above.
  function applyStampaFontSizing($grid, fontSizePx) {
    $grid.addClass('stampa-sized-grid');
    $('#stampa-font-style').remove();

    var css =
      '.stampa-sized-grid .scheduling-grid th, .stampa-sized-grid .scheduling-grid td { font-size: ' + fontSizePx + 'px; }' +
      '.stampa-sized-grid .pianificazione-session-overlay, .stampa-sized-grid .pianificazione-cell-label { font-size: ' + fontSizePx + 'px; }' +
      '.stampa-sized-grid .giorno-vacation-label { font-size: ' + Math.round(fontSizePx * 0.85) + 'px; }' +
      '.stampa-sized-grid .pianificazione-reparto-count { font-size: ' + Math.round(fontSizePx * 0.9) + 'px; }';

    $('<style id="stampa-font-style">' + css + '</style>').appendTo('head');
  }

  // Combines the three steps above (compute sizing, build with a custom row height,
  // apply matching font sizing) - used by all three Giorno print modes below so none
  // of them have to repeat this sequence.
  function buildStampaGiornoGrid(data, vacationMatch, labelFn) {
    var sizing = computeStampaGiornoSizing(data.gridEnd - data.gridStart);
    var $grid = buildGiornoGridTable(data, vacationMatch, labelFn, sizing.rowHeightPx);
    applyStampaFontSizing($grid, sizing.fontSizePx);
    return $grid;
  }

  // Same as buildStampaGiornoGrid above, off computeStampaSettimanaSizing instead.
  function buildStampaSettimanaGrid(data, vacationMatchFn, labelFn) {
    var sizing = computeStampaSettimanaSizing(data.gridEnd - data.gridStart);
    var $grid = buildSettimanaGridTable(data, vacationMatchFn, labelFn, sizing.rowHeightPx);
    applyStampaFontSizing($grid, sizing.fontSizePx);
    return $grid;
  }

  function renderStampaView() {
    var $stampaBtn = $('<button type="button" class="primary">Stampa</button>');
    $stampaBtn.on('click', function() {
      window.print();
    });

    var $chiudiBtn = $('<button type="button" class="secondary">Chiudi anteprima</button>');
    $chiudiBtn.on('click', function() {
      navigateTo(printSourceView === 'settimana' ? 'settimana' : 'giorno');
    });

    var $topbarButtons = $('<div class="stampa-topbar-actions"></div>').append($stampaBtn).append($chiudiBtn);
    setTopbarActions($topbarButtons);

    if (printSourceView === 'settimana') {
      setStampaPageOrientation('landscape');
      buildStampaSettimanaContent();
    } else {
      setStampaPageOrientation('portrait');
      buildStampaGiornoContent();
    }
  }

  function buildStampaHeader(entityName, dateLabel) {
    return $('<div class="stampa-header"></div>')
      .append($('<div class="stampa-header-entity"></div>').text(entityName))
      .append($('<div class="stampa-header-date"></div>').text(dateLabel));
  }

  // Piano's own header - deliberately separate from buildStampaHeader above (not
  // reused by Giorno/Settimana) so this doesn't risk touching those already-working
  // print views. Title on the left, Centro Minerva logo + caption on the right.
  function buildPianoHeader(titleText) {
    var $logo = $('<div class="piano-header-logo"></div>')
      .append($('<img src="LogoCentroMinerva.png" alt="Centro Minerva">'))
      .append($('<div class="piano-header-logo-caption"></div>').text('Centro Minerva'));

    return $('<div class="piano-header"></div>')
      .append($('<div class="stampa-header-entity"></div>').text(titleText))
      .append($logo);
  }

  function buildStampaGiornoContent() {
    if (!giornoMode) {
      $('#content').empty().append('<div class="scheduling-empty">Nessun calendario da stampare.</div>');
      return;
    }

    var dateLabel = formatGiornoLabel(giornoCurrentDate);

    if (giornoMode === 'therapist') {
      $.get('Giorno/TherapistData', { therapistId: giornoSelectedTherapistId, date: formatDateISO(giornoCurrentDate) })
        .done(function(data) {
          if (!data.hasAvailability) {
            $('#content').empty().append('<div class="scheduling-empty">Nessuna disponibilità per il terapista ' + data.name + '</div>');
            return;
          }

          var vacationMatch = findVacationForDate(data.vacations, giornoCurrentDate, data.name);
          var $grid = buildStampaGiornoGrid(data, vacationMatch, therapistModeLabel);
          $('#content').empty().append(buildStampaHeader(data.name, dateLabel)).append($grid);
        });
      return;
    }

    $.get('Giorno/RepartoData', { sex: giornoSelectedRepartoSex, date: formatDateISO(giornoCurrentDate) })
      .done(function(data) {
        var vacationMatch = findVacationForDate(data.vacations, giornoCurrentDate, '');
        var $grid = buildStampaGiornoGrid(data, vacationMatch, repartoModeLabel);
        $('#content').empty().append(buildStampaHeader(data.name, dateLabel)).append($grid);
      });
  }

  function buildStampaSettimanaContent() {
    if (!settimanaMode) {
      $('#content').empty().append('<div class="scheduling-empty">Nessun calendario da stampare.</div>');
      return;
    }

    var weekStartISO = formatDateISO(settimanaCurrentWeekStart);
    var weekEnd = addDays(settimanaCurrentWeekStart, 4);
    var weekLabel = formatSettimanaLabel(settimanaCurrentWeekStart, weekEnd);

    if (settimanaMode === 'therapist') {
      $.get('Settimana/TherapistData', { therapistId: settimanaSelectedTherapistId, weekStart: weekStartISO })
        .done(function(data) {
          if (!data.hasAvailability) {
            $('#content').empty().append('<div class="scheduling-empty">Nessuna disponibilità per il terapista ' + data.name + ' questa settimana.</div>');
            return;
          }

          var $grid = buildStampaSettimanaGrid(data, function(colDate) {
            return findVacationForDate(data.vacations, colDate, data.name);
          }, therapistModeLabel);
          $('#content').empty().append(buildStampaHeader(data.name, weekLabel)).append($grid);
        });
      return;
    }

    $.get('Settimana/RepartoData', { sex: settimanaSelectedRepartoSex, weekStart: weekStartISO })
      .done(function(data) {
        var $grid = buildStampaSettimanaGrid(data, function(colDate) {
          return findVacationForDate(data.vacations, colDate, '');
        }, repartoModeLabel);
        $('#content').empty().append(buildStampaHeader(data.name, weekLabel)).append($grid);
      });
  }

  // --- Piano (patient plan) ----------------------------------------------------
  //
  // Reached via the "Genera piano" button on the patient form: every slot of the
  // patient's current therapy (see Patients/PlanData), sorted by date/time. Unlike
  // Giorno/Settimana's Stampa, this is a plain list, not a time grid - so no
  // fit-to-one-page sizing here, it just flows across pages normally when printed.
  // Reuses the same Stampa/Chiudi button pattern, buildStampaHeader, and
  // setStampaPageOrientation (portrait) for consistency.
  function renderPianoView(patientId) {
    var $stampaBtn = $('<button type="button" class="primary">Stampa</button>');
    $stampaBtn.on('click', function() {
      window.print();
    });

    var $chiudiBtn = $('<button type="button" class="secondary">Chiudi</button>');
    $chiudiBtn.on('click', function() {
      navigateTo('pazienti', patientId);
    });

    var $topbarButtons = $('<div class="stampa-topbar-actions"></div>').append($stampaBtn).append($chiudiBtn);
    setTopbarActions($topbarButtons);
    setStampaPageOrientation('portrait');

    $.get('Patients/PlanData/' + patientId).done(function(data) {
      var titleText = data.patientName + ' - Piano Terapeutico' + (data.therapyName ? ' - ' + data.therapyName : '');
      var $header = buildPianoHeader(titleText);

      if (data.slots.length === 0) {
        $('#content').empty().append($header).append('<div class="scheduling-empty">Nessuna sessione pianificata.</div>');
        return;
      }

      var $table = $('<table class="piano-table"></table>');
      $table.append('<thead><tr><th>Data</th><th>Ora</th><th>Terapia</th><th>Operatore</th></tr></thead>');

      var $tbody = $('<tbody></tbody>');
      data.slots.forEach(function(s) {
        var slotDate = parseDateISO(s.date);
        var $row = $('<tr></tr>');
        $row.append($('<td></td>').text(formatPlanDate(slotDate)));
        $row.append($('<td></td>').text(slotToTime(s.timeSlot)));
        $row.append($('<td></td>').text(s.therapyTypeName));
        $row.append($('<td></td>').text(s.therapistName));
        $tbody.append($row);
      });
      $table.append($tbody);

      $('#content').empty().append($header).append($table);
    });
  }

  // --- Avvisi (Alerts) --------------------------------------------------------

  // Shared by the card's "Vai" button and every jump-button inside the detail
  // popup - a single place that knows how to route to a patient, or to a
  // therapist's week/day at a specific date.
  function navigateToAlertTarget(view, id, therapistId, date) {
    if (view === 'settimana' && therapistId) {
      settimanaCurrentWeekStart = getMondayOfWeek(parseDateOnly(date));
      settimanaMode = 'therapist';
      settimanaSelectedTherapistId = therapistId;
      navigateTo('settimana');
    } else if (view === 'giorno' && therapistId) {
      giornoCurrentDate = parseDateOnly(date);
      giornoMode = 'therapist';
      giornoSelectedTherapistId = therapistId;
      navigateTo('giorno');
    } else {
      navigateTo(view, id);
    }
  }

  function alertJumpBtn(label, view, id, therapistId, date) {
    var $btn = $('<button type="button" class="alert-popup-jump-btn"></button>').text(label);
    $btn.on('click', function() {
      $('#modal-overlay').hide();
      navigateToAlertTarget(view, id, therapistId, date);
    });
    return $btn;
  }

  function dismissAlert(key) {
    $.ajax({ url: 'Alerts/Dismiss', method: 'POST', contentType: 'application/json', data: JSON.stringify({ key: key }) })
      .done(function() {
        $('#modal-overlay').hide();
        renderAvvisiList();
      });
  }

  function buildAlertCard(alert, isImportant) {
    var $card = $('<div class="alert-card"></div>');
    $card.css('border-left-color', alert.color);
    if (isImportant) {
      $card.addClass('alert-card-important');
    }

    var $header = $('<div class="alert-card-header"></div>');
    $header.append($('<span class="alert-card-icon"></span>').text(alert.icon));
    $header.append($('<span class="alert-card-title"></span>').text(alert.title));
    $card.append($header);

    $card.append($('<div class="alert-card-line1"></div>').text(alert.line1 || ''));
    if (alert.line2) {
      $card.append($('<div class="alert-card-line2"></div>').text(alert.line2));
    } else {
      $card.append('<div class="alert-card-line2"></div>');
    }

    var $actions = $('<div class="alert-card-actions"></div>');

    var $goBtn = $('<button type="button" class="alert-card-go-btn">Vai</button>');
    $goBtn.on('click', function(e) {
      e.stopPropagation();
      navigateToAlertTarget(alert.targetView, alert.targetId, alert.targetTherapistId, alert.targetDate);
    });
    $actions.append($goBtn);

    if (isImportant) {
      var $gestitoBtn = $('<button type="button" class="alert-card-go-btn secondary">Gestito</button>');
      $gestitoBtn.on('click', function(e) {
        e.stopPropagation();
        $.ajax({ url: 'Alerts/Gestito', method: 'POST', contentType: 'application/json', data: JSON.stringify({ key: alert.dismissKey }) })
          .done(function() { renderAvvisiList(); });
      });
      $actions.append($gestitoBtn);
    } else {
      var $importantBtn = $('<button type="button" class="alert-card-go-btn secondary">Importante!</button>');
      $importantBtn.on('click', function(e) {
        e.stopPropagation();
        $.ajax({ url: 'Alerts/MarkImportant', method: 'POST', contentType: 'application/json', data: JSON.stringify({ key: alert.dismissKey }) })
          .done(function() { renderAvvisiList(); });
      });
      $actions.append($importantBtn);
    }

    $card.append($actions);

    $card.on('click', function() {
      showAlertDetailPopup(alert);
    });

    return $card;
  }

  function renderAvvisiList() {
    setTopbarActions(null);
    $('#content').empty().append('<div class="scheduling-empty">Caricamento avvisi...</div>');

    $.get('Alerts/List').done(function(result) {
      var alerts = result.alerts || [];
      var importantAlerts = result.importantAlerts || [];

      if (alerts.length === 0 && importantAlerts.length === 0) {
        $('#content').empty().append('<div class="scheduling-empty">Nessun avviso al momento.</div>');
        return;
      }

      var $wrapper = $('<div></div>');

      if (importantAlerts.length > 0) {
        var $importantGrid = $('<div class="alerts-grid alerts-grid-important"></div>');
        importantAlerts.forEach(function(alert) {
          $importantGrid.append(buildAlertCard(alert, true));
        });
        $wrapper.append($importantGrid);
      }

      if (alerts.length > 0) {
        var $grid = $('<div class="alerts-grid"></div>');
        alerts.forEach(function(alert) {
          $grid.append(buildAlertCard(alert, false));
        });
        $wrapper.append($grid);
      }

      $('#content').empty().append($wrapper);
    });
  }

  // Builds the wider description sentence for the detail popup, with each named
  // entity (patient, therapist) as its own clickable jump button rather than a
  // single generic "Vai" (CPU's call).
  function buildAlertDetailBody(alert) {
    var $p = $('<p></p>');

    if (alert.type === 'therapyToBeScheduled') {
      $p.append(alertJumpBtn(alert.line1, 'pazienti', alert.targetId));
      $p.append(' ha una terapia (' + alert.partsDetail + ') da pianificare.');
    } else if (alert.type === 'repeatedNoShow2') {
      var dates = (alert.absentDates || []).map(function(d) { return formatDayMonthLowercase(parseDateISO(d)); });
      $p.append(alertJumpBtn(alert.line1, 'pazienti', alert.targetId));
      $p.append(' non si è presentato per 2 sedute, il ' + dates.join(' e ') + '. Contattarlo per confermare che voglia continuare la terapia.');
    } else if (alert.type === 'repeatedNoShow3') {
      $p.append(alertJumpBtn(alert.line1, 'pazienti', alert.targetId));
      $p.append(' non si è presentato a ' + alert.absentCount + ' o più sedute. Deve essere rimosso dal calendario.');
    } else if (alert.type === 'vacationConflict') {
      $p.append(alertJumpBtn(alert.line1, 'settimana', null, alert.targetTherapistId, alert.targetDate));
      $p.append(' è assente dal ' + alert.line2 + ', ma ha una seduta pianificata con ');
      $p.append(alertJumpBtn(alert.conflictPatientName, 'pazienti', alert.conflictPatientId));
      $p.append('.');
    } else if (alert.type === 'therapyRenewal') {
      $p.append('La terapia di ');
      $p.append(alertJumpBtn(alert.line1, 'pazienti', alert.targetId));
      $p.append(' è in scadenza (ultima seduta pianificata: ' + alert.line2 + ').');
    } else if (alert.type === 'therapistUnderScheduled') {
      $p.append(alertJumpBtn(alert.line1, 'settimana', null, alert.targetTherapistId, alert.targetDate));
      $p.append(' non ha pazienti a partire da ' + formatGiornoLabel(parseDateISO(alert.targetDate)) + '.');
    } else if (alert.type === 'pastHolidayToUpdate') {
      $p.append('La festività ');
      $p.append(alertJumpBtn(alert.line1, 'assenze', alert.targetId));
      $p.append(' è passata e va aggiornata per l\'anno prossimo.');
    } else {
      $p.text(alert.detail);
    }

    return $p;
  }

  function showAlertDetailPopup(alert) {
    var $body = $('<div></div>');
    $body.append($('<h3></h3>').text(alert.icon + ' ' + alert.title));
    $body.append(buildAlertDetailBody(alert));

    var isWhatsappType = (alert.type === 'repeatedNoShow2' || alert.type === 'repeatedNoShow3') && alert.patientPhone;
    var $textarea = null;

    if (isWhatsappType) {
      $body.append('<p>Telefono: ' + formatPhoneDisplay(alert.patientPhone) + '</p>');
      $body.append('<p>Messaggio da inviare con Whatsapp:</p>');
      $textarea = $('<textarea rows="6" style="width:100%;"></textarea>').val(buildAssenzeWhatsappMessage(alert));
      $body.append($textarea);
    }

    // Dismetti is pushed first and pinned left via .modal-btn-left (margin-right:
    // auto in a flex-end row) - everything else (Vai, WhatsApp, Chiudi) stays
    // grouped on the right, per CPU's layout call.
    var actions = [];

    actions.push({
      label: 'Dismetti',
      className: 'danger modal-btn-left',
      onClick: function() { dismissAlert(alert.dismissKey); }
    });

    if (alert.targetView) {
      actions.push({
        label: 'Vai',
        className: '',
        onClick: function() {
          navigateToAlertTarget(alert.targetView, alert.targetId, alert.targetTherapistId, alert.targetDate);
        }
      });
    }

    if (isWhatsappType) {
      actions.push({
        label: 'Invia su WhatsApp',
        className: '',
        onClick: function() {
          var phone = (alert.patientPhone || '').replace(/[^0-9]/g, '');
          var url = 'https://wa.me/' + phone + '?text=' + encodeURIComponent($textarea.val());
          window.open(url, '_blank');
        }
      });
    }

    actions.push({ label: 'Chiudi', className: 'secondary', onClick: function() {} });

    window.showModal('', actions);
    $('#modal-body').empty().append($body);
  }

  function buildAssenzeWhatsappMessage(alert) {
    var isFemale = alert.patientSex === 1;

    if (alert.type === 'repeatedNoShow3') {
      var participio = isFemale ? 'presentata' : 'presentato';
      var rimosso = isFemale ? 'rimossa' : 'rimosso';

      return 'Lei non si è ' + participio + ' ' + alert.absentCount + ' volte alle sedute di terapia. ' +
        'Come da contratto da lei sottoscritto, lei verrà ' + rimosso + ' dal calendario finché la sua posizione non sarà regolarizzata. ' +
        'Al momento della regolarizzazione la nuova pianificazione sarà decisa. Cordialmente, Centro Minerva.';
    }

    var participioSingolo = isFemale ? 'presentata' : 'presentato';
    var dayLabel = alert.lastAbsentDate ? formatSimpleDate(alert.lastAbsentDate) : '';
    var therapyLabel = alert.lastAbsentTherapyTypeName || '';

    return 'Lei non si è ' + participioSingolo + ' alla seduta di ' + therapyLabel + ' del ' + dayLabel + '. ' +
      'Ci conferma che continuerà a seguire le visite? Cordialmente Centro Minerva.';
  }

  // --- Pianificazione (scheduling a single-part Palestra Therapy) ------------

  // --- Pianificazione (scheduling a single-part Palestra Therapy) ------------

  function renderPianificazioneView(partId) {
    var info = null;
    var therapistList = null;
    var therapistId = null;
    var showAlsoTherapistId = null;
    var showAlsoWeekData = null;
    var weekStart = getMondayOfWeek(new Date());
    var weekJustChanged = false;
    var mode = 'singolo';
    var rimpiazza = true;
    var draft = []; // { date: 'yyyy-MM-dd', timeSlot, conflict, selfConflict, _dragId }
    var lastError = '';
    var lastWeekData = null;
    var draggedEntry = null;
    var dragIdCounter = 0;
    var currentTdRefs = null;
    var currentGridMin = 0;
    var $dropHighlight = null;

    function showDropHighlight(col, startRow, span) {
      if (!currentTdRefs || !currentTdRefs[col] || !currentTdRefs[col][startRow]) {
        return;
      }
      var $startTd = currentTdRefs[col][startRow];
      if (!$dropHighlight) {
        $dropHighlight = $('<div class="pianificazione-drop-highlight"></div>');
      }
      $dropHighlight.css({
        top: '0',
        left: '0',
        width: '100%',
        height: (span * PIANIFICAZIONE_ROW_HEIGHT - 1) + 'px'
      });
      $startTd.append($dropHighlight);
    }

    function hideDropHighlight() {
      if ($dropHighlight) {
        $dropHighlight.detach();
      }
    }

    function navigateWeek(newWeekStart) {
      weekStart = newWeekStart;
      weekJustChanged = true;
      fetchShowAlsoData();
      render();
    }

    function fetchShowAlsoData() {
      if (!showAlsoTherapistId) {
        showAlsoWeekData = null;
        renderGrid();
        return;
      }

      $.get('Pianificazione/WeekData', { therapistId: showAlsoTherapistId, partId: partId, weekStart: formatDateISO(weekStart) })
        .done(function(weekData) {
          showAlsoWeekData = weekData;
          renderGrid();
        });
    }

    function currentTherapistName() {
      if (!therapistList) {
        return '';
      }
      var match = therapistList.filter(function(t) { return t.id === therapistId; });
      return match.length ? match[0].name : '';
    }

    function isConflict(d) {
      return !!(d.conflict || d.selfConflict);
    }

    // Two of our own not-yet-committed draft entries can overlap each other even
    // though the server never sees them together - checked purely client-side.
    function recomputeSelfConflicts() {
      draft.forEach(function(d) { d.selfConflict = false; });

      for (var i = 0; i < draft.length; i++) {
        for (var j = i + 1; j < draft.length; j++) {
          var a = draft[i];
          var b = draft[j];

          // Same day is enough - every draft entry belongs to the same Part/TherapyType
          // in this flow, and a patient can't do the same TherapyType twice in one day
          // even at non-overlapping times.
          if (a.date === b.date) {
            a.selfConflict = true;
            b.selfConflict = true;
          }
        }
      }
    }

    function buildSessionLabel(patientName, therapistName, therapyTypeName) {
      return patientName + ' - ' + therapistName + ' - ' + therapyTypeName;
    }

    function render() {
      renderHeader();
      renderGrid();
    }

    function renderHeader() {
      var $header = $('<div class="scheduling-header"></div>');

      var $therapistSelect = $('<select class="scheduling-therapist-select"></select>');
      $therapistSelect.append('<option value="">-- Seleziona terapista --</option>');
      therapistList.forEach(function(t) {
        $therapistSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
      });
      $therapistSelect.val(therapistId || '');
      $therapistSelect.on('change', function() {
        var val = $therapistSelect.val();
        therapistId = val ? parseInt(val, 10) : null;
        draft = []; // switching therapist means a different calendar entirely
        lastError = '';
        render();
      });

      var $showAlsoLabel = $('<span class="pianificazione-showalso-label">E anche</span>');
      var $showAlsoSelect = $('<select class="scheduling-therapist-select"></select>');
      $showAlsoSelect.append('<option value="">Nessuno</option>');
      therapistList.forEach(function(t) {
        $showAlsoSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
      });
      $showAlsoSelect.val(showAlsoTherapistId || '');
      $showAlsoSelect.on('change', function() {
        var val = $showAlsoSelect.val();
        showAlsoTherapistId = val ? parseInt(val, 10) : null;
        fetchShowAlsoData();
      });

      var $modeSelect = $(
        '<select>' +
        '<option value="singolo">Giorno singolo</option>' +
        '<option value="giorniAlterni">Giorni Alterni</option>' +
        '<option value="settimanalmente">Settimanalmente</option>' +
        '<option value="tuttiIGiorni">Tutti i giorni</option>' +
        '</select>'
      );
      $modeSelect.val(mode);
      $modeSelect.on('change', function() {
        mode = $modeSelect.val();
        refreshRimpiazzaState();
      });

      var $rimpiazzaRow = $('<label class="pianificazione-rimpiazza-row"></label>');
      var $rimpiazzaCheckbox = $('<input type="checkbox">');
      $rimpiazzaCheckbox.prop('checked', rimpiazza);
      $rimpiazzaCheckbox.on('change', function() {
        rimpiazza = $rimpiazzaCheckbox.is(':checked');
      });
      $rimpiazzaRow.append($rimpiazzaCheckbox).append(' Rimpiazza');

      function refreshRimpiazzaState() {
        $rimpiazzaCheckbox.prop('disabled', mode === 'singolo');
        $rimpiazzaRow.toggleClass('disabled', mode === 'singolo');
      }
      refreshRimpiazzaState();

      var weekEnd = addDays(weekStart, 4);
      var $dateLabel = $('<span class="scheduling-date-label"></span>').text(formatSettimanaLabel(weekStart, weekEnd));

      var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
      $prev.on('click', function() {
        navigateWeek(addDays(weekStart, -7));
      });

      var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
      $next.on('click', function() {
        navigateWeek(addDays(weekStart, 7));
      });

      var $today = $('<button type="button" class="secondary">Vai ad Oggi</button>');
      $today.on('click', function() {
        navigateWeek(getMondayOfWeek(new Date()));
      });

      var $resetBtn = $('<button type="button" class="secondary">Azzera pianificazione</button>');
      $resetBtn.on('click', function() {
        if (draft.length === 0) {
          return;
        }
        if (window.confirm('Cancellare tutte le sedute pianificate finora in questa sessione?')) {
          draft = [];
          lastError = '';
          render();
        }
      });

      var remainingAfterDraft = info.remaining - draft.length;
      var hasAnyConflict = draft.some(isConflict);

      var $acceptBtn = $('<button type="button">Accetta Pianificazione</button>');
      $acceptBtn.prop('disabled', !therapistId || draft.length === 0 || remainingAfterDraft !== 0 || hasAnyConflict);
      $acceptBtn.on('click', onAccept);

      var $cancelBtn = $('<button type="button" class="danger">Annulla</button>');
      $cancelBtn.on('click', function() {
        if (draft.length > 0 && !window.confirm('Uscire senza salvare la pianificazione in corso?')) {
          return;
        }
        navigateTo('pazienti', info.patientId);
      });

      $header
        .append($therapistSelect).append($showAlsoLabel).append($showAlsoSelect).append($modeSelect).append($rimpiazzaRow)
        .append($prev).append($dateLabel).append($next).append($today)
        .append($resetBtn).append($acceptBtn).append($cancelBtn);
      setTopbarActions($header);
    }

    function appendGridContent($wrapper, shouldFade) {
      $('#content').empty().append($wrapper);

      if (shouldFade) {
        $wrapper.addClass('pianificazione-week-fade');
        requestAnimationFrame(function() {
          $wrapper.addClass('pianificazione-week-fade-in');
        });
      }
    }

    function renderGrid() {
      var shouldFade = weekJustChanged;
      weekJustChanged = false;

      var $wrapper = $('<div class="scheduling-view pianificazione-wrapper"></div>');

      var $progress = $('<div class="pianificazione-progress"></div>');
      $progress.text(info.therapyTypeName + ' - ' + (draft.length + info.placedCount) + ' / ' + info.sessionCount + ' sedute pianificate');
      if (draft.some(isConflict)) {
        $progress.append($('<span class="conflict-warning"> (sono presenti conflitti da risolvere)</span>'));
      }
      $wrapper.append($progress);

      if (lastError) {
        $wrapper.append('<div class="form-error" style="display:block;">' + lastError + '</div>');
      }

      if (!therapistId) {
        $wrapper.append('<div class="scheduling-empty">Seleziona un terapista per iniziare la pianificazione.</div>');
        appendGridContent($wrapper, shouldFade);
        return;
      }

      $.get('Pianificazione/WeekData', { therapistId: therapistId, partId: partId, weekStart: formatDateISO(weekStart) })
        .done(function(weekData) {
          lastWeekData = weekData;
          buildGridTable($wrapper, weekData, shouldFade);
        });
    }

    function attachDragAndDropHandlers($cell, dateStr, slot, draftEntry) {
      if (draftEntry) {
        $cell.attr('draggable', 'true');

        $cell.on('dragstart', function(e) {
          draggedEntry = draftEntry;
          if (e.originalEvent && e.originalEvent.dataTransfer) {
            e.originalEvent.dataTransfer.effectAllowed = 'move';
          }
        });

        $cell.on('dragend', function() {
          draggedEntry = null;
          hideDropHighlight();
        });
      }

      $cell.on('dragover', function(e) {
        e.preventDefault();
        var col = weekDays.findIndex(function(d, i) { return formatDateISO(addDays(weekStart, i)) === dateStr; });
        var row = slot - currentGridMin;
        showDropHighlight(col, row, Math.ceil(info.duration / 15));
      });

      $cell.on('drop', function(e) {
        e.preventDefault();
        hideDropHighlight();
        handleDrop(dateStr, slot);
      });
    }

    function handleDrop(targetDateStr, targetSlot) {
      if (!draggedEntry) {
        return;
      }

      draggedEntry.date = targetDateStr;
      draggedEntry.timeSlot = targetSlot;

      var spanSlots = Math.ceil(info.duration / 15);
      var conflict = false;

      if (lastWeekData) {
        var dateObj = parseDateOnly(targetDateStr);
        var dow = dateObj.getDay();

        var withinAvail = lastWeekData.availability.some(function(a) {
          return a.dayOfWeek === dow && targetSlot >= a.startTime && (targetSlot + spanSlots) <= a.endTime;
        });

        if (!withinAvail) {
          conflict = true;
        } else {
          var vac = findVacationForDate(lastWeekData.vacations, dateObj, currentTherapistName());
          if (vac) {
            var cov = vacationCoverage(vac, 0, 96);
            if (cov && targetSlot < cov.coverEnd && (targetSlot + spanSlots) > cov.coverStart) {
              conflict = true;
            }
          }
        }

        if (!conflict) {
          var overlapsExisting = lastWeekData.slots.some(function(slotInfo) {
            if (slotInfo.date !== targetDateStr) {
              return false;
            }

            // Same TherapyType on the same day is a conflict even without a time
            // overlap (a patient can't do the same TherapyType twice in one day).
            if (slotInfo.therapyTypeId === info.therapyTypeId) {
              return true;
            }

            var existingEnd = slotInfo.timeSlot + slotInfo.durationSlots;
            return targetSlot < existingEnd && (targetSlot + spanSlots) > slotInfo.timeSlot;
          });
          if (overlapsExisting) {
            conflict = true;
          }
        }
      }

      draggedEntry.conflict = conflict;
      draggedEntry = null;

      recomputeSelfConflicts();
      render();
    }

    function buildGridTable($wrapper, weekData, shouldFade) {
      var spanSlots = Math.ceil(info.duration / 15);
      var todayStr = formatDateISO(new Date());

      // Per-day visible range: that day's first-available slot minus 1, to last-available
      // slot plus 1 (per day, not shared) - then unioned into one shared row set for the grid.
      var dayRanges = [];
      for (var i = 0; i < 5; i++) {
        var dow = i + 1;
        var dayAvail = weekData.availability.filter(function(a) { return a.dayOfWeek === dow; });

        if (dayAvail.length === 0) {
          dayRanges.push(null);
        } else {
          var dayMin = Math.max(0, Math.min.apply(null, dayAvail.map(function(a) { return a.startTime; })) - 1);
          var dayMax = Math.min(96, Math.max.apply(null, dayAvail.map(function(a) { return a.endTime; })) + 1);
          dayRanges.push({ min: dayMin, max: dayMax });
        }
      }

      var validRanges = dayRanges.filter(function(r) { return r !== null; });

      if (validRanges.length === 0) {
        $wrapper.append('<div class="scheduling-empty">Nessuna disponibilità per il terapista selezionato.</div>');
        appendGridContent($wrapper, shouldFade);
        return;
      }

      var gridMin = Math.min.apply(null, validRanges.map(function(r) { return r.min; }));
      var gridMax = Math.max.apply(null, validRanges.map(function(r) { return r.max; }));
      var rowCount = gridMax - gridMin;
      currentGridMin = gridMin;

      // Per column: bgCells (vacation/unavailable/empty, one per row - stays a plain,
      // individually-precise drop target) and items (existing + draft sessions, each
      // ONE entry spanning its full duration - rendered as an absolutely-positioned
      // overlay div rather than repeated per-row cells).
      var columns = [];

      for (var c = 0; c < 5; c++) {
        var colDate = addDays(weekStart, c);
        var colDateStr = formatDateISO(colDate);
        var bgCells = [];

        for (var s = 0; s < rowCount; s++) {
          var slot = gridMin + s;
          var withinAvail = dayRanges[c] && weekData.availability.some(function(a) {
            return a.dayOfWeek === (c + 1) && slot >= a.startTime && (slot + 1) <= a.endTime;
          });

          if (!withinAvail) {
            bgCells.push({ kind: 'unavailable' });
            continue;
          }

          var vac = findVacationForDate(weekData.vacations, colDate, currentTherapistName());
          if (vac) {
            var cov = vacationCoverage(vac, gridMin, gridMax);
            if (cov && slot >= cov.coverStart && slot < cov.coverEnd) {
              bgCells.push({ kind: 'vacation', label: vac.label });
              continue;
            }
          }

          bgCells.push({ kind: 'empty' });
        }

        var items = [];

        weekData.slots.forEach(function(slotInfo) {
          if (slotInfo.date !== colDateStr) {
            return;
          }
          items.push({
            key: 'existing-' + slotInfo.id,
            startSlot: slotInfo.timeSlot,
            endSlot: slotInfo.timeSlot + slotInfo.durationSlots,
            kind: slotInfo.isCurrentPatient ? 'occupied-same' : 'occupied-other',
            label: buildSessionLabel(slotInfo.patientName, slotInfo.therapistName, slotInfo.therapyTypeName)
          });
        });

        draft.forEach(function(d) {
          if (d.date !== colDateStr) {
            return;
          }
          items.push({
            key: 'draft-' + d._dragId,
            startSlot: d.timeSlot,
            endSlot: d.timeSlot + spanSlots,
            kind: isConflict(d) ? 'draft-conflict' : 'draft',
            label: buildSessionLabel(info.patientName, currentTherapistName(), info.therapyTypeName),
            draftRef: d
          });
        });

        if (showAlsoWeekData) {
          showAlsoWeekData.slots.forEach(function(slotInfo) {
            if (slotInfo.date !== colDateStr || slotInfo.therapistId !== showAlsoTherapistId) {
              return;
            }
            items.push({
              key: 'showalso-' + slotInfo.id,
              startSlot: slotInfo.timeSlot,
              endSlot: slotInfo.timeSlot + slotInfo.durationSlots,
              kind: 'showalso',
              label: buildSessionLabel(slotInfo.patientName, slotInfo.therapistName, slotInfo.therapyTypeName)
            });
          });
        }

        columns.push({
          bgCells: bgCells,
          items: items,
          groups: computeOverlapGroups(items),
          isToday: colDateStr === todayStr
        });
      }

      var $table = $('<table class="scheduling-grid pianificazione-grid"></table>');
      var $thead = $('<thead></thead>');
      var $headerRow = $('<tr></tr>');

      var $orarioHeader = $('<th>Orario</th>');
      $headerRow.append($orarioHeader);

      weekDays.forEach(function(day, i) {
        var colDate = addDays(weekStart, i);
        var isToday = formatDateISO(colDate) === todayStr;
        var $th = $('<th' + (isToday ? ' class="pianificazione-today-header"' : '') + '>' + day.label + ' ' + formatDateDDMM(colDate) + '</th>');
        $headerRow.append($th);
      });

      $thead.append($headerRow);
      $table.append($thead);

      var $tbody = $('<tbody></tbody>');
      var tdRefs = []; // tdRefs[col][row] - needed afterward to inject overlay divs at each item's start row

      for (var col0 = 0; col0 < 5; col0++) {
        tdRefs.push([]);
      }

      for (var row = 0; row < rowCount; row++) {
        var slotVal = gridMin + row;
        var $row = $('<tr></tr>');
        var $timeCell = $('<td class="scheduling-time-cell">' + slotToTime(slotVal) + '</td>');
        $row.append($timeCell);

        for (var col = 0; col < 5; col++) {
          var bgInfo = columns[col].bgCells[row];
          var todayClass = columns[col].isToday ? ' pianificazione-today-col' : '';
          var cellDateStr = formatDateISO(addDays(weekStart, col));
          var $td;

          if (bgInfo.kind === 'vacation') {
            $td = $('<td class="vacation-overlay' + todayClass + '" title="' + bgInfo.label + '"></td>');
          } else if (bgInfo.kind === 'unavailable') {
            $td = $('<td class="availability-overlay' + todayClass + '"></td>');
          } else {
            $td = $('<td class="pianificazione-empty-cell' + todayClass + '"></td>');
            (function(clickDate, clickSlot) {
              $td.on('click', function() {
                placeAt(clickDate, clickSlot);
              });
            })(addDays(weekStart, col), slotVal);
          }

          attachDragAndDropHandlers($td, cellDateStr, slotVal, null);

          $row.append($td);
          tdRefs[col][row] = $td;
        }

        $tbody.append($row);
      }

      currentTdRefs = tdRefs;
      $table.append($tbody);

      // Inject one overlay div per item, anchored at its start row's <td> (which has
      // position:relative), sized in pixels to visually span down over subsequent rows.
      for (var c2 = 0; c2 < 5; c2++) {
        var colDateStr2 = formatDateISO(addDays(weekStart, c2));

        columns[c2].items.forEach(function(item) {
          var rowIndex = item.startSlot - gridMin;
          if (rowIndex < 0 || rowIndex >= rowCount) {
            return;
          }

          var span = item.endSlot - item.startSlot;
          var groupInfo = columns[c2].groups[item.key];
          var widthPct = 100 / groupInfo.groupSize;
          var leftPct = groupInfo.indexInGroup * widthPct;

          var cssClass = item.kind === 'occupied-other' ? 'pianificazione-occupied-other'
            : item.kind === 'occupied-same' ? 'pianificazione-occupied-same'
            : item.kind === 'draft' ? 'pianificazione-draft'
            : item.kind === 'showalso' ? 'pianificazione-showalso'
            : 'pianificazione-conflict';

          var $overlay = $('<div class="pianificazione-session-overlay ' + cssClass + '" title="' + item.label + '"><span class="pianificazione-cell-label">' + item.label + '</span></div>');
          $overlay.css({
            top: '0',
            left: leftPct + '%',
            width: widthPct + '%',
            height: (span * PIANIFICAZIONE_ROW_HEIGHT - 1) + 'px'
          });

          if (item.draftRef) {
            var $removeBtn = $('<button type="button" class="availability-remove">×</button>');
            (function(draftRef) {
              $removeBtn.on('click', function(e) {
                e.stopPropagation();
                draft = draft.filter(function(d) { return d !== draftRef; });
                recomputeSelfConflicts();
                render();
              });
            })(item.draftRef);
            $overlay.append($removeBtn);
          }

          if (item.kind !== 'showalso') {
            attachDragAndDropHandlers($overlay, colDateStr2, item.startSlot, item.draftRef || null);
          }

          var $startTd = tdRefs[c2][rowIndex];
          if ($startTd) {
            $startTd.append($overlay);
          }
        });
      }

      $wrapper.append($table);
      appendGridContent($wrapper, shouldFade);
      positionEdgeOverlays($table);
    }

    function placeAt(dateObj, timeSlot) {
      var dateStr = formatDateISO(dateObj);

      if (mode === 'singolo') {
        // A clickable "empty" cell is by definition not occupied by anything already
        // fetched, so no server round-trip is needed for a manual single placement.
        // Rimpiazza has no effect in Giorno singolo - each click just adds one slot.
        draft.push({ date: dateStr, timeSlot: timeSlot, conflict: false, _dragId: dragIdCounter++ });
        lastError = '';
        recomputeSelfConflicts();
        render();
        return;
      }

      if (rimpiazza) {
        draft = [];
      }

      var neededCount = rimpiazza ? info.remaining : (info.remaining - draft.length);

      if (neededCount <= 0) {
        return;
      }

      $.ajax({
        url: 'Pianificazione/AutoFill',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({
          therapyPartId: partId,
          therapistId: therapistId,
          mode: mode,
          startDate: dateStr,
          timeSlot: timeSlot,
          count: neededCount
        })
      })
        .done(function(placements) {
          placements.forEach(function(p) {
            p._dragId = dragIdCounter++;
          });

          draft = rimpiazza ? placements : draft.concat(placements);
          lastError = '';
          recomputeSelfConflicts();
          render();
        })
        .fail(function(jqXHR) {
          lastError = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Operazione non riuscita.';
          render();
        });
    }

    function onAccept() {
      $.ajax({
        url: 'Pianificazione/Accept',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({
          therapyPartId: partId,
          therapistId: therapistId,
          placements: draft.map(function(d) { return { date: d.date, timeSlot: d.timeSlot }; })
        })
      })
        .done(function() {
          navigateTo('pazienti', info.patientId);
        })
        .fail(function(jqXHR) {
          lastError = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Salvataggio non riuscito.';
          renderGrid();
        });
    }

    setupEdgeOverlays(function(side) {
      navigateWeek(addDays(weekStart, side === 'left' ? -7 : 7));
    });

    $.get('Pianificazione/Info/' + partId).done(function(infoResult) {
      info = infoResult;

      $.get('Pianificazione/TherapistsFor/' + partId).done(function(list) {
        therapistList = list;
        render();
      });
    });
  }

  // --- Pianificazione Reparto (Light Reparto: no therapist, capacity gradient) --

  function renderPianificazioneRepartoView(therapyId) {
    var info = null; // { therapyId, patientId, patientName, parts: [...] }
    var partsRemaining = {}; // partId -> remaining count
    var weekStart = getMondayOfWeek(new Date());
    var weekJustChanged = false;
    var mode = 'singolo';
    var rimpiazza = true;
    var draft = []; // flat list: { partId, therapyTypeId, therapyTypeName, durationSlots, date, timeSlot, conflict, selfConflict, _dragId }
    var lastError = '';
    var lastWeekData = null;
    var draggedEntry = null;
    var dragIdCounter = 0;
    var currentTdRefs = null;
    var currentGridMin = 0;
    var $dropHighlight = null;

    function showDropHighlight(col, startRow, span) {
      if (!currentTdRefs || !currentTdRefs[col] || !currentTdRefs[col][startRow]) {
        return;
      }
      var $startTd = currentTdRefs[col][startRow];
      if (!$dropHighlight) {
        $dropHighlight = $('<div class="pianificazione-drop-highlight"></div>');
      }
      $dropHighlight.css({
        top: '0',
        left: '0',
        width: '100%',
        height: (span * PIANIFICAZIONE_ROW_HEIGHT - 1) + 'px'
      });
      $startTd.append($dropHighlight);
    }

    function hideDropHighlight() {
      if ($dropHighlight) {
        $dropHighlight.detach();
      }
    }


    function navigateWeek(newWeekStart) {
      weekStart = newWeekStart;
      weekJustChanged = true;
      render();
    }

    function isConflict(d) {
      return !!(d.conflict || d.selfConflict);
    }

    function resetPartsRemaining() {
      partsRemaining = {};
      info.parts.forEach(function(p) {
        partsRemaining[p.id] = p.remaining;
      });
    }

    function totalRemaining() {
      var sum = 0;
      Object.keys(partsRemaining).forEach(function(pid) {
        sum += partsRemaining[pid];
      });
      return sum;
    }

    function buildRepartoSessionLabel(patientName, therapyTypeName) {
      return patientName + ' - ' + therapyTypeName;
    }

    // Read-only for now (CPU hasn't decided what interactions belong here yet) -
    // built as its own modal call so actions can be added later without a rework.
    function showRepartoSlotPopup(dateObj, sessions) {
      var title = 'Terapie di ' + formatGiornoLabel(dateObj);

      var itemsHtml = sessions.map(function(s) {
        var line = s.patientName + ' - ' + s.therapyTypeName;
        if (s.therapistName && s.therapistName !== 'Reparto') {
          line += ' - ' + s.therapistName;
        }
        return '<li>' + line + '</li>';
      }).join('');

      window.showModal(
        '<h3>' + title + '</h3><ul>' + itemsHtml + '</ul>',
        [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]
      );
    }

    function capacityColorClass(capacity, demand, warningThresholdPercent) {
      var free = capacity - demand;
      if (free <= 0) {
        return 'pianificazione-capacity-red';
      }
      var occupancyRatio = capacity > 0 ? demand / capacity : 1;
      var thresholdRatio = (warningThresholdPercent || 75) / 100;
      if (occupancyRatio >= thresholdRatio) {
        return 'pianificazione-capacity-yellow'; // near/at the configured saturation warning threshold
      }
      return ''; // white - plenty of room (old green + yellow buckets merged)
    }

    // Same TherapyType twice a day, or an overlapping time range, is a conflict -
    // matches the backend rule, applied client-side across the whole flat draft list.
    function recomputeSelfConflicts() {
      draft.forEach(function(d) { d.selfConflict = false; });

      for (var i = 0; i < draft.length; i++) {
        for (var j = i + 1; j < draft.length; j++) {
          var a = draft[i];
          var b = draft[j];

          if (a.date !== b.date) {
            continue;
          }

          if (a.therapyTypeId === b.therapyTypeId) {
            a.selfConflict = true;
            b.selfConflict = true;
            continue;
          }

          var aEnd = a.timeSlot + a.durationSlots;
          var bEnd = b.timeSlot + b.durationSlots;
          if (a.timeSlot < bEnd && aEnd > b.timeSlot) {
            a.selfConflict = true;
            b.selfConflict = true;
          }
        }
      }
    }

    function render() {
      renderHeader();
      renderGrid();
    }

    function renderHeader() {
      var $header = $('<div class="scheduling-header"></div>');

      var $modeSelect = $(
        '<select>' +
        '<option value="singolo">Giorno singolo</option>' +
        '<option value="giorniAlterni">Giorni Alterni</option>' +
        '<option value="settimanalmente">Settimanalmente</option>' +
        '<option value="tuttiIGiorni">Tutti i giorni</option>' +
        '</select>'
      );
      $modeSelect.val(mode);
      $modeSelect.on('change', function() {
        mode = $modeSelect.val();
        refreshRimpiazzaState();
      });

      var $rimpiazzaRow = $('<label class="pianificazione-rimpiazza-row"></label>');
      var $rimpiazzaCheckbox = $('<input type="checkbox">');
      $rimpiazzaCheckbox.prop('checked', rimpiazza);
      $rimpiazzaCheckbox.on('change', function() {
        rimpiazza = $rimpiazzaCheckbox.is(':checked');
      });
      $rimpiazzaRow.append($rimpiazzaCheckbox).append(' Rimpiazza');

      function refreshRimpiazzaState() {
        $rimpiazzaCheckbox.prop('disabled', mode === 'singolo');
        $rimpiazzaRow.toggleClass('disabled', mode === 'singolo');
      }
      refreshRimpiazzaState();

      var weekEnd = addDays(weekStart, 4);
      var $dateLabel = $('<span class="scheduling-date-label"></span>').text(formatSettimanaLabel(weekStart, weekEnd));

      var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
      $prev.on('click', function() {
        navigateWeek(addDays(weekStart, -7));
      });

      var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
      $next.on('click', function() {
        navigateWeek(addDays(weekStart, 7));
      });

      var $today = $('<button type="button" class="secondary">Vai ad Oggi</button>');
      $today.on('click', function() {
        navigateWeek(getMondayOfWeek(new Date()));
      });

      var $resetBtn = $('<button type="button" class="secondary">Azzera pianificazione</button>');
      $resetBtn.on('click', function() {
        if (draft.length === 0) {
          return;
        }
        if (window.confirm('Cancellare tutte le sedute pianificate finora in questa sessione?')) {
          draft = [];
          resetPartsRemaining();
          lastError = '';
          render();
        }
      });

      var hasAnyConflict = draft.some(isConflict);
      var $acceptBtn = $('<button type="button">Accetta Pianificazione</button>');
      $acceptBtn.prop('disabled', draft.length === 0 || totalRemaining() !== 0 || hasAnyConflict);
      $acceptBtn.on('click', onAccept);

      var $cancelBtn = $('<button type="button" class="danger">Annulla</button>');
      $cancelBtn.on('click', function() {
        if (draft.length > 0 && !window.confirm('Uscire senza salvare la pianificazione in corso?')) {
          return;
        }
        navigateTo('pazienti', info.patientId);
      });

      $header
        .append($modeSelect).append($rimpiazzaRow)
        .append($prev).append($dateLabel).append($next).append($today)
        .append($resetBtn).append($acceptBtn).append($cancelBtn);
      setTopbarActions($header);
    }

    function appendGridContent($wrapper, shouldFade) {
      $('#content').empty().append($wrapper);

      if (shouldFade) {
        $wrapper.addClass('pianificazione-week-fade');
        requestAnimationFrame(function() {
          $wrapper.addClass('pianificazione-week-fade-in');
        });
      }
    }

    function renderGrid() {
      var shouldFade = weekJustChanged;
      weekJustChanged = false;

      var $wrapper = $('<div class="scheduling-view pianificazione-wrapper"></div>');

      var progressText = info.parts.map(function(p) {
        var draftCountForPart = draft.filter(function(d) { return d.partId === p.id; }).length;
        return p.therapyTypeName + ': ' + (p.placedCount + draftCountForPart) + '/' + p.sessionCount;
      }).join(' - ');

      var $progress = $('<div class="pianificazione-progress"></div>');
      $progress.text(progressText);
      if (draft.some(isConflict)) {
        $progress.append($('<span class="conflict-warning"> (sono presenti conflitti da risolvere)</span>'));
      }
      $wrapper.append($progress);

      if (lastError) {
        $wrapper.append('<div class="form-error" style="display:block;">' + lastError + '</div>');
      }

      $.get('Pianificazione/RepartoWeekData', { therapyId: therapyId, weekStart: formatDateISO(weekStart) })
        .done(function(weekData) {
          lastWeekData = weekData;
          buildGridTable($wrapper, weekData, shouldFade);
        });
    }

    function attachDragAndDropHandlers($cell, dateStr, slot, draftEntry) {
      if (draftEntry) {
        $cell.attr('draggable', 'true');

        $cell.on('dragstart', function(e) {
          draggedEntry = draftEntry;
          if (e.originalEvent && e.originalEvent.dataTransfer) {
            e.originalEvent.dataTransfer.effectAllowed = 'move';
          }
        });

        $cell.on('dragend', function() {
          draggedEntry = null;
          hideDropHighlight();
        });
      }

      $cell.on('dragover', function(e) {
        e.preventDefault();
        if (!draggedEntry) {
          return;
        }
        var col = weekDays.findIndex(function(d, i) { return formatDateISO(addDays(weekStart, i)) === dateStr; });
        var row = slot - currentGridMin;
        showDropHighlight(col, row, draggedEntry.durationSlots);
      });

      $cell.on('drop', function(e) {
        e.preventDefault();
        hideDropHighlight();
        handleDrop(dateStr, slot);
      });
    }

    function handleDrop(targetDateStr, targetSlot) {
      if (!draggedEntry || !lastWeekData) {
        return;
      }

      draggedEntry.date = targetDateStr;
      draggedEntry.timeSlot = targetSlot;

      var conflict = false;
      var span = draggedEntry.durationSlots;

      for (var slot = targetSlot; slot < targetSlot + span; slot++) {
        var capEntry = lastWeekData.capacityGrid.filter(function(c) { return c.date === targetDateStr && c.timeSlot === slot; })[0];
        if (capEntry && (capEntry.capacity - capEntry.demand) <= 0) {
          conflict = true;
        }
      }

      if (!conflict) {
        var overlapsExisting = lastWeekData.slots.some(function(slotInfo) {
          if (slotInfo.date !== targetDateStr) {
            return false;
          }
          if (slotInfo.therapyTypeId === draggedEntry.therapyTypeId) {
            return true;
          }
          var existingEnd = slotInfo.timeSlot + slotInfo.durationSlots;
          return targetSlot < existingEnd && (targetSlot + span) > slotInfo.timeSlot;
        });
        if (overlapsExisting) {
          conflict = true;
        }
      }

      draggedEntry.conflict = conflict;
      draggedEntry = null;

      recomputeSelfConflicts();
      render();
    }

    // --- Grid ------------------------------------------------------------------

    function buildGridTable($wrapper, weekData, shouldFade) {
      var todayStr = formatDateISO(new Date());
      var capIndex = {};
      weekData.capacityGrid.forEach(function(c) {
        capIndex[c.date + '|' + c.timeSlot] = c;
      });

      var gridMin = weekData.dayStart;
      var gridMax = weekData.dayEnd;
      var rowCount = gridMax - gridMin;
      currentGridMin = gridMin;

      var columns = [];
      for (var c = 0; c < 5; c++) {
        var colDate = addDays(weekStart, c);
        var colDateStr = formatDateISO(colDate);
        var bgCells = [];

        for (var s = 0; s < rowCount; s++) {
          var slotVal0 = gridMin + s;
          var capEntry = capIndex[colDateStr + '|' + slotVal0];
          var capacity = capEntry ? capEntry.capacity : 0;
          var demand = capEntry ? capEntry.demand : 0;

          bgCells.push({
            capacityClass: capacityColorClass(capacity, demand, weekData.capacityWarningThreshold),
            blocked: (capacity - demand) <= 0,
            existingCount: 0,
            existingSessions: []
          });
        }

        // Every existing session overlapping a slot is collected (not overwritten) -
        // the count just shows how many, the popup lists them all.
        weekData.slots.forEach(function(slotInfo) {
          if (slotInfo.date !== colDateStr) {
            return;
          }
          var startIdx = slotInfo.timeSlot - gridMin;
          for (var k = 0; k < slotInfo.durationSlots; k++) {
            var idx = startIdx + k;
            if (idx >= 0 && idx < rowCount) {
              bgCells[idx].existingSessions.push(slotInfo);
              bgCells[idx].existingCount++;
            }
          }
        });

        var items = [];

        draft.forEach(function(d) {
          if (d.date !== colDateStr) {
            return;
          }
          items.push({
            key: 'draft-' + d._dragId,
            startSlot: d.timeSlot,
            endSlot: d.timeSlot + d.durationSlots,
            kind: isConflict(d) ? 'draft-conflict' : 'draft',
            label: buildRepartoSessionLabel(info.patientName, d.therapyTypeName),
            draftRef: d
          });
        });

        (weekData.palestraHelperSlots || []).forEach(function(slotInfo) {
          if (slotInfo.date !== colDateStr) {
            return;
          }
          items.push({
            key: 'palestrahelper-' + slotInfo.id,
            startSlot: slotInfo.timeSlot,
            endSlot: slotInfo.timeSlot + slotInfo.durationSlots,
            kind: 'showalso',
            label: slotInfo.patientName + ' - ' + slotInfo.therapistName + ' - ' + slotInfo.therapyTypeName
          });
        });

        columns.push({
          bgCells: bgCells,
          items: items,
          groups: computeOverlapGroups(items),
          isToday: colDateStr === todayStr
        });
      }

      var $table = $('<table class="scheduling-grid pianificazione-grid"></table>');

      var $thead = $('<thead></thead>');
      var $headerRow = $('<tr></tr>');
      var $orarioHeader = $('<th>Orario</th>');
      $headerRow.append($orarioHeader);

      weekDays.forEach(function(day, i) {
        var colDate = addDays(weekStart, i);
        var isToday = formatDateISO(colDate) === todayStr;
        var $th = $('<th' + (isToday ? ' class="pianificazione-today-header"' : '') + '>' + day.label + ' ' + formatDateDDMM(colDate) + '</th>');
        $headerRow.append($th);
      });

      $thead.append($headerRow);
      $table.append($thead);

      var $tbody = $('<tbody></tbody>');
      var tdRefs = [];

      for (var col0 = 0; col0 < 5; col0++) {
        tdRefs.push([]);
      }

      for (var row = 0; row < rowCount; row++) {
        var slotVal = gridMin + row;
        var $row = $('<tr></tr>');
        var $timeCell = $('<td class="scheduling-time-cell">' + slotToTime(slotVal) + '</td>');
        $row.append($timeCell);

        for (var col = 0; col < 5; col++) {
          var bgInfo = columns[col].bgCells[row];
          var todayClass = columns[col].isToday ? ' pianificazione-today-col' : '';
          var cellDateStr = formatDateISO(addDays(weekStart, col));

          var $td = $('<td class="pianificazione-reparto-cell ' + bgInfo.capacityClass + todayClass + '"></td>');

          if (bgInfo.existingCount > 0) {
            var $count = $('<span class="pianificazione-reparto-count">' + bgInfo.existingCount + '</span>');
            (function(sessions, cellDate) {
              $count.on('click', function(e) {
                e.stopPropagation();
                showRepartoSlotPopup(cellDate, sessions);
              });
            })(bgInfo.existingSessions, addDays(weekStart, col));
            $td.append($count);
          }

          if (!bgInfo.blocked && totalRemaining() > 0) {
            (function(clickDate, clickSlot) {
              $td.on('click', function() {
                placeAt(clickDate, clickSlot);
              });
            })(addDays(weekStart, col), slotVal);
          }

          attachDragAndDropHandlers($td, cellDateStr, slotVal, null);

          $row.append($td);
          tdRefs[col][row] = $td;
        }

        $tbody.append($row);
      }

      currentTdRefs = tdRefs;
      $table.append($tbody);

      // Draft overlay divs - each spans its full duration in pixels, width split
      // among any overlapping drafts within calc(100% - 2em) (2em reserved on the
      // right for the count, kept clear of the overlay entirely).
      for (var c2 = 0; c2 < 5; c2++) {
        var colDateStr2 = formatDateISO(addDays(weekStart, c2));

        columns[c2].items.forEach(function(item) {
          var rowIndex = item.startSlot - gridMin;
          if (rowIndex < 0 || rowIndex >= rowCount) {
            return;
          }

          var span = item.endSlot - item.startSlot;
          var groupInfo = columns[c2].groups[item.key];
          var groupSize = groupInfo.groupSize;
          var idx = groupInfo.indexInGroup;

          var draftClass = item.kind === 'draft' ? 'pianificazione-draft'
            : item.kind === 'showalso' ? 'pianificazione-showalso'
            : 'pianificazione-conflict';
          var $overlay = $('<div class="pianificazione-session-overlay pianificazione-reparto-overlay ' + draftClass + '" title="' + item.label + '"><span class="pianificazione-cell-label">' + item.label + '</span></div>');

          $overlay.css({
            top: '0',
            height: (span * PIANIFICAZIONE_ROW_HEIGHT - 1) + 'px',
            left: 'calc((100% - 2em) * ' + idx + ' / ' + groupSize + ')',
            width: 'calc((100% - 2em) / ' + groupSize + ')'
          });

          if (item.draftRef) {
            var $removeBtn = $('<button type="button" class="availability-remove">×</button>');
            (function(draftRef) {
              $removeBtn.on('click', function(e) {
                e.stopPropagation();
                draft = draft.filter(function(dd) { return dd !== draftRef; });
                partsRemaining[draftRef.partId]++;
                recomputeSelfConflicts();
                render();
              });
            })(item.draftRef);
            $overlay.append($removeBtn);

            attachDragAndDropHandlers($overlay, colDateStr2, item.startSlot, item.draftRef);
          }

          var $startTd = tdRefs[c2][rowIndex];
          if ($startTd) {
            $startTd.append($overlay);
          }
        });
      }

      $wrapper.append($table);
      appendGridContent($wrapper, shouldFade);
      positionEdgeOverlays($table);
    }

    // --- Placement ---------------------------------------------------------

    function placeAt(dateObj, timeSlot) {
      var dateStr = formatDateISO(dateObj);

      if (mode === 'singolo') {
        var activePartsList = info.parts.filter(function(p) { return partsRemaining[p.id] > 0; });
        if (activePartsList.length === 0) {
          return;
        }

        var cursor = timeSlot;
        var newEntries = [];
        var anyBlocked = false;

        activePartsList.forEach(function(p) {
          var span = Math.ceil(p.duration / 15);
          for (var s = cursor; s < cursor + span; s++) {
            var capEntry = lastWeekData ? lastWeekData.capacityGrid.filter(function(c) { return c.date === dateStr && c.timeSlot === s; })[0] : null;
            if (capEntry && (capEntry.capacity - capEntry.demand) <= 0) {
              anyBlocked = true;
            }
          }
          newEntries.push({
            partId: p.id,
            therapyTypeId: p.therapyTypeId,
            therapyTypeName: p.therapyTypeName,
            durationSlots: span,
            date: dateStr,
            timeSlot: cursor,
            conflict: false,
            _dragId: dragIdCounter++
          });
          cursor += span;
        });

        if (anyBlocked) {
          lastError = 'Capacità Reparto satura in questo orario.';
          render();
          return;
        }

        newEntries.forEach(function(e) {
          draft.push(e);
          partsRemaining[e.partId]--;
        });

        lastError = '';
        recomputeSelfConflicts();
        render();
        return;
      }

      if (rimpiazza) {
        draft = [];
        resetPartsRemaining();
      }

      var remainingOverride = info.parts.map(function(p) {
        return { partId: p.id, remaining: partsRemaining[p.id] };
      });

      $.ajax({
        url: 'Pianificazione/RepartoAutoFill',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({
          therapyId: therapyId,
          mode: mode,
          startDate: dateStr,
          timeSlot: timeSlot,
          count: 0,
          remainingOverride: remainingOverride
        })
      })
        .done(function(occurrences) {
          occurrences.forEach(function(occ) {
            occ.segments.forEach(function(seg) {
              draft.push({
                partId: seg.partId,
                therapyTypeId: seg.therapyTypeId,
                therapyTypeName: seg.therapyTypeName,
                durationSlots: seg.durationSlots,
                date: occ.date,
                timeSlot: seg.timeSlot,
                conflict: occ.conflict,
                _dragId: dragIdCounter++
              });
              partsRemaining[seg.partId]--;
            });
          });

          lastError = '';
          recomputeSelfConflicts();
          render();
        })
        .fail(function(jqXHR) {
          lastError = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Operazione non riuscita.';
          render();
        });
    }

    function onAccept() {
      var placementsByDate = {};

      draft.forEach(function(d) {
        if (!placementsByDate[d.date]) {
          placementsByDate[d.date] = [];
        }
        placementsByDate[d.date].push({ partId: d.partId, timeSlot: d.timeSlot });
      });

      var placements = Object.keys(placementsByDate).map(function(date) {
        return { date: date, segments: placementsByDate[date] };
      });

      $.ajax({
        url: 'Pianificazione/RepartoAccept',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({ therapyId: therapyId, placements: placements })
      })
        .done(function() {
          navigateTo('pazienti', info.patientId);
        })
        .fail(function(jqXHR) {
          lastError = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Salvataggio non riuscito.';
          renderGrid();
        });
    }

    setupEdgeOverlays(function(side) {
      navigateWeek(addDays(weekStart, side === 'left' ? -7 : 7));
    });

    $.get('Pianificazione/RepartoInfo/' + therapyId).done(function(infoResult) {
      info = infoResult;
      resetPartsRemaining();
      render();
    });
  }

  // --- Pianificazione Mixed (Heavy + Light) -----------------------------------

  function renderPianificazioneMixedView(therapyId) {
    var info = null; // { therapyId, patientId, patientName, heavyParts, lightParts, hasPalestraHeavy, hasRepartoHeavy }
    var therapistsInfo = null; // { palestraTherapists, repartoTherapists }
    var palestraTherapistId = null;
    var repartoTherapistId = null; // null = auto-pick
    var partsRemaining = {};
    var weekStart = getMondayOfWeek(new Date());
    var weekJustChanged = false;
    var mode = 'singolo';
    var rimpiazza = true;
    var draft = []; // flat list: { kind: 'heavy'|'light', partId, therapyTypeId, therapyTypeName, durationSlots, date, timeSlot, therapistId, therapistName, conflict, selfConflict, _dragId }
    var lastError = '';
    var lastWeekData = null;
    var draggedEntry = null;
    var dragIdCounter = 0;
    var currentTdRefs = null;
    var currentGridMin = 0;
    var $dropHighlight = null;

    function showDropHighlight(col, startRow, span) {
      if (!currentTdRefs || !currentTdRefs[col] || !currentTdRefs[col][startRow]) {
        return;
      }
      var $startTd = currentTdRefs[col][startRow];
      if (!$dropHighlight) {
        $dropHighlight = $('<div class="pianificazione-drop-highlight"></div>');
      }
      $dropHighlight.css({
        top: '0',
        left: '0',
        width: '100%',
        height: (span * PIANIFICAZIONE_ROW_HEIGHT - 1) + 'px'
      });
      $startTd.append($dropHighlight);
    }

    function hideDropHighlight() {
      if ($dropHighlight) {
        $dropHighlight.detach();
      }
    }

    function navigateWeek(newWeekStart) {
      weekStart = newWeekStart;
      weekJustChanged = true;
      render();
    }

    function isConflict(d) {
      return !!(d.conflict || d.selfConflict);
    }

    function resetPartsRemaining() {
      partsRemaining = {};
      info.heavyParts.forEach(function(p) { partsRemaining[p.id] = p.remaining; });
      info.lightParts.forEach(function(p) { partsRemaining[p.id] = p.remaining; });
    }

    function totalRemaining() {
      var sum = 0;
      Object.keys(partsRemaining).forEach(function(pid) { sum += partsRemaining[pid]; });
      return sum;
    }

    function heavyRemaining() {
      return info.heavyParts.reduce(function(sum, p) { return sum + partsRemaining[p.id]; }, 0);
    }

    function buildMixedSessionLabel(therapistName, therapyTypeName) {
      return info.patientName + ' - ' + therapistName + ' - ' + therapyTypeName;
    }

    function showMixedSlotPopup(dateObj, sessions) {
      var title = 'Terapie di ' + formatGiornoLabel(dateObj);
      var itemsHtml = sessions.map(function(s) {
        var line = s.patientName + ' - ' + s.therapyTypeName;
        if (s.therapistName && s.therapistName !== 'Reparto') {
          line += ' - ' + s.therapistName;
        }
        return '<li>' + line + '</li>';
      }).join('');

      window.showModal(
        '<h3>' + title + '</h3><ul>' + itemsHtml + '</ul>',
        [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]
      );
    }

    function capacityColorClass(capacity, demand, warningThresholdPercent) {
      var free = capacity - demand;
      if (free <= 0) {
        return 'pianificazione-capacity-red';
      }
      var occupancyRatio = capacity > 0 ? demand / capacity : 1;
      var thresholdRatio = (warningThresholdPercent || 75) / 100;
      if (occupancyRatio >= thresholdRatio) {
        return 'pianificazione-capacity-yellow';
      }
      return '';
    }

    function recomputeSelfConflicts() {
      draft.forEach(function(d) { d.selfConflict = false; });

      for (var i = 0; i < draft.length; i++) {
        for (var j = i + 1; j < draft.length; j++) {
          var a = draft[i];
          var b = draft[j];

          if (a.date !== b.date) {
            continue;
          }

          if (a.therapyTypeId === b.therapyTypeId) {
            a.selfConflict = true;
            b.selfConflict = true;
            continue;
          }

          var aEnd = a.timeSlot + a.durationSlots;
          var bEnd = b.timeSlot + b.durationSlots;
          if (a.timeSlot < bEnd && aEnd > b.timeSlot) {
            a.selfConflict = true;
            b.selfConflict = true;
          }
        }
      }
    }

    function render() {
      renderHeader();
      renderGrid();
    }

    function renderHeader() {
      var $header = $('<div class="scheduling-header"></div>');

      if (info.hasPalestraHeavy) {
        var $palestraSelect = $('<select class="scheduling-therapist-select"></select>');
        $palestraSelect.append('<option value="">-- Seleziona terapista Palestra --</option>');
        therapistsInfo.palestraTherapists.forEach(function(t) {
          $palestraSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
        });
        $palestraSelect.val(palestraTherapistId || '');
        $palestraSelect.on('change', function() {
          var val = $palestraSelect.val();
          palestraTherapistId = val ? parseInt(val, 10) : null;
          draft = [];
          resetPartsRemaining();
          lastError = '';
          render();
        });
        $header.append($palestraSelect);
      }

      if (info.hasRepartoHeavy) {
        var $repartoSelect = $('<select class="scheduling-therapist-select"></select>');
        $repartoSelect.append('<option value="">-- Auto (Reparto) --</option>');
        therapistsInfo.repartoTherapists.forEach(function(t) {
          $repartoSelect.append('<option value="' + t.id + '">' + t.name + (t.isHelper ? ' (aiuto)' : '') + '</option>');
        });
        $repartoSelect.val(repartoTherapistId || '');
        $repartoSelect.on('change', function() {
          var val = $repartoSelect.val();
          repartoTherapistId = val ? parseInt(val, 10) : null;
          draft = [];
          resetPartsRemaining();
          lastError = '';
          render();
        });
        $header.append($repartoSelect);
      }

      var $modeSelect = $(
        '<select>' +
        '<option value="singolo">Giorno singolo</option>' +
        '<option value="giorniAlterni">Giorni Alterni</option>' +
        '<option value="settimanalmente">Settimanalmente</option>' +
        '<option value="tuttiIGiorni">Tutti i giorni</option>' +
        '</select>'
      );
      $modeSelect.val(mode);
      $modeSelect.on('change', function() {
        mode = $modeSelect.val();
        refreshRimpiazzaState();
      });

      var $rimpiazzaRow = $('<label class="pianificazione-rimpiazza-row"></label>');
      var $rimpiazzaCheckbox = $('<input type="checkbox">');
      $rimpiazzaCheckbox.prop('checked', rimpiazza);
      $rimpiazzaCheckbox.on('change', function() {
        rimpiazza = $rimpiazzaCheckbox.is(':checked');
      });
      $rimpiazzaRow.append($rimpiazzaCheckbox).append(' Rimpiazza');

      function refreshRimpiazzaState() {
        $rimpiazzaCheckbox.prop('disabled', mode === 'singolo');
        $rimpiazzaRow.toggleClass('disabled', mode === 'singolo');
      }
      refreshRimpiazzaState();

      var weekEnd = addDays(weekStart, 4);
      var $dateLabel = $('<span class="scheduling-date-label"></span>').text(formatSettimanaLabel(weekStart, weekEnd));

      var $prev = $('<button type="button" class="scheduling-nav-btn">‹</button>');
      $prev.on('click', function() { navigateWeek(addDays(weekStart, -7)); });

      var $next = $('<button type="button" class="scheduling-nav-btn">›</button>');
      $next.on('click', function() { navigateWeek(addDays(weekStart, 7)); });

      var $today = $('<button type="button" class="secondary">Vai ad Oggi</button>');
      $today.on('click', function() { navigateWeek(getMondayOfWeek(new Date())); });

      var $resetBtn = $('<button type="button" class="secondary">Azzera pianificazione</button>');
      $resetBtn.on('click', function() {
        if (draft.length === 0) {
          return;
        }
        if (window.confirm('Cancellare tutte le sedute pianificate finora in questa sessione?')) {
          draft = [];
          resetPartsRemaining();
          lastError = '';
          render();
        }
      });

      var hasAnyConflict = draft.some(isConflict);
      var $acceptBtn = $('<button type="button">Accetta Pianificazione</button>');
      $acceptBtn.prop('disabled', draft.length === 0 || totalRemaining() !== 0 || hasAnyConflict);
      $acceptBtn.on('click', onAccept);

      var $cancelBtn = $('<button type="button" class="danger">Annulla</button>');
      $cancelBtn.on('click', function() {
        if (draft.length > 0 && !window.confirm('Uscire senza salvare la pianificazione in corso?')) {
          return;
        }
        navigateTo('pazienti', info.patientId);
      });

      $header
        .append($modeSelect).append($rimpiazzaRow)
        .append($prev).append($dateLabel).append($next).append($today)
        .append($resetBtn).append($acceptBtn).append($cancelBtn);
      setTopbarActions($header);
    }

    function appendGridContent($wrapper, shouldFade) {
      $('#content').empty().append($wrapper);
      if (shouldFade) {
        $wrapper.addClass('pianificazione-week-fade');
        requestAnimationFrame(function() {
          $wrapper.addClass('pianificazione-week-fade-in');
        });
      }
    }

    function renderGrid() {
      var shouldFade = weekJustChanged;
      weekJustChanged = false;

      var $wrapper = $('<div class="scheduling-view pianificazione-wrapper"></div>');

      var progressParts = info.heavyParts.concat(info.lightParts).map(function(p) {
        var draftCountForPart = draft.filter(function(d) { return d.partId === p.id; }).length;
        return p.therapyTypeName + ': ' + (p.placedCount + draftCountForPart) + '/' + p.sessionCount;
      }).join(' - ');

      var $progress = $('<div class="pianificazione-progress"></div>');
      $progress.text(progressParts);
      if (draft.some(isConflict)) {
        $progress.append($('<span class="conflict-warning"> (sono presenti conflitti da risolvere)</span>'));
      }
      $wrapper.append($progress);

      if (lastError) {
        $wrapper.append('<div class="form-error" style="display:block;">' + lastError + '</div>');
      }

      if (info.hasPalestraHeavy && !palestraTherapistId) {
        $wrapper.append('<div class="scheduling-empty">Seleziona un terapista per la Palestra per iniziare la pianificazione.</div>');
        appendGridContent($wrapper, shouldFade);
        return;
      }

      var weekDataParams = { therapyId: therapyId, weekStart: formatDateISO(weekStart) };
      if (palestraTherapistId) {
        weekDataParams.palestraTherapistId = palestraTherapistId;
      }
      if (repartoTherapistId) {
        weekDataParams.repartoTherapistId = repartoTherapistId;
      }

      $.get('Pianificazione/MixedWeekData', weekDataParams).done(function(weekData) {
        lastWeekData = weekData;
        buildGridTable($wrapper, weekData, shouldFade);
      });
    }

    function attachDragAndDropHandlers($cell, dateStr, slot, draftEntry) {
      if (draftEntry) {
        $cell.attr('draggable', 'true');
        $cell.on('dragstart', function(e) {
          draggedEntry = draftEntry;
          if (e.originalEvent && e.originalEvent.dataTransfer) {
            e.originalEvent.dataTransfer.effectAllowed = 'move';
          }
        });
        $cell.on('dragend', function() {
          draggedEntry = null;
          hideDropHighlight();
        });
      }

      $cell.on('dragover', function(e) {
        e.preventDefault();
        if (!draggedEntry) {
          return;
        }
        var col = weekDays.findIndex(function(d, i) { return formatDateISO(addDays(weekStart, i)) === dateStr; });
        var row = slot - currentGridMin;
        showDropHighlight(col, row, draggedEntry.durationSlots);
      });
      $cell.on('drop', function(e) {
        e.preventDefault();
        hideDropHighlight();
        handleDrop(dateStr, slot);
      });
    }

    // Dragging moves exactly one segment (Heavy or Light) - matches the general
    // "each therapy slot can be dragged and dropped individually" convention.
    function handleDrop(targetDateStr, targetSlot) {
      if (!draggedEntry || !lastWeekData) {
        return;
      }

      var entry = draggedEntry;
      entry.date = targetDateStr;
      entry.timeSlot = targetSlot;
      draggedEntry = null;

      if (entry.kind === 'light') {
        var conflict = false;
        for (var s = targetSlot; s < targetSlot + entry.durationSlots; s++) {
          var capEntry = lastWeekData.capacityGrid.filter(function(c) { return c.date === targetDateStr && c.timeSlot === s; })[0];
          if (capEntry && (capEntry.capacity - capEntry.demand) <= 0) {
            conflict = true;
          }
        }
        if (!conflict) {
          conflict = lastWeekData.slots.some(function(slotInfo) {
            if (slotInfo.date !== targetDateStr) { return false; }
            if (slotInfo.therapyTypeId === entry.therapyTypeId) { return true; }
            var existingEnd = slotInfo.timeSlot + slotInfo.durationSlots;
            return targetSlot < existingEnd && (targetSlot + entry.durationSlots) > slotInfo.timeSlot;
          });
        }
        entry.conflict = conflict;
        recomputeSelfConflicts();
        render();
        return;
      }

      // Heavy: re-check the specific therapist already assigned to this segment
      // (fixed Palestra therapist, or whichever Reparto-Active therapist was picked).
      $.get('Pianificazione/MixedTherapistCheck', {
        therapistId: entry.therapistId,
        date: targetDateStr,
        timeSlot: targetSlot,
        spanSlots: entry.durationSlots
      }).done(function(result) {
        entry.conflict = !result.ok;
        recomputeSelfConflicts();
        render();
      }).fail(function() {
        entry.conflict = true;
        recomputeSelfConflicts();
        render();
      });
    }

    // --- Grid ------------------------------------------------------------------

    function buildGridTable($wrapper, weekData, shouldFade) {
      var todayStr = formatDateISO(new Date());
      var capIndex = {};
      weekData.capacityGrid.forEach(function(c) { capIndex[c.date + '|' + c.timeSlot] = c; });

      var gridMin = weekData.dayStart;
      var gridMax = weekData.dayEnd;
      var rowCount = gridMax - gridMin;
      currentGridMin = gridMin;

      var columns = [];
      for (var c = 0; c < 5; c++) {
        var colDate = addDays(weekStart, c);
        var colDateStr = formatDateISO(colDate);
        var bgCells = [];

        for (var s = 0; s < rowCount; s++) {
          var slotVal0 = gridMin + s;
          var capEntry = capIndex[colDateStr + '|' + slotVal0];
          var capacity = capEntry ? capEntry.capacity : 0;
          var demand = capEntry ? capEntry.demand : 0;

          // Reparto capacity is purely informational here (it only ever gates Light
          // placement, computed server-side) - it never blocks clicking to place Heavy.
          var palestraBlocked = false;
          if (info.hasPalestraHeavy && palestraTherapistId) {
            var dow = colDate.getDay();
            var withinAvail = weekData.palestraAvailability.some(function(a) {
              return a.dayOfWeek === dow && slotVal0 >= a.startTime && (slotVal0 + 1) <= a.endTime;
            });
            if (!withinAvail) {
              palestraBlocked = true;
            } else {
              var vac = findVacationForDate(weekData.vacations, colDate, currentPalestraTherapistName());
              if (vac) {
                var cov = vacationCoverage(vac, gridMin, gridMax);
                if (cov && slotVal0 >= cov.coverStart && slotVal0 < cov.coverEnd) {
                  palestraBlocked = true;
                }
              }
            }
          }

          bgCells.push({
            capacityClass: capacityColorClass(capacity, demand, weekData.capacityWarningThreshold),
            palestraBlocked: palestraBlocked,
            existingCount: 0,
            existingSessions: []
          });
        }

        weekData.slots.forEach(function(slotInfo) {
          if (slotInfo.date !== colDateStr) { return; }
          var startIdx = slotInfo.timeSlot - gridMin;
          for (var k = 0; k < slotInfo.durationSlots; k++) {
            var idx = startIdx + k;
            if (idx >= 0 && idx < rowCount) {
              bgCells[idx].existingSessions.push(slotInfo);
              bgCells[idx].existingCount++;
            }
          }
        });

        var items = [];

        draft.forEach(function(d) {
          if (d.date !== colDateStr) { return; }
          items.push({
            key: 'draft-' + d._dragId,
            startSlot: d.timeSlot,
            endSlot: d.timeSlot + d.durationSlots,
            kind: isConflict(d) ? 'draft-conflict' : 'draft',
            label: buildMixedSessionLabel(d.therapistName, d.therapyTypeName),
            draftRef: d
          });
        });

        (weekData.palestraHelperSlots || []).forEach(function(slotInfo) {
          if (slotInfo.date !== colDateStr) { return; }
          items.push({
            key: 'palestrahelper-' + slotInfo.id,
            startSlot: slotInfo.timeSlot,
            endSlot: slotInfo.timeSlot + slotInfo.durationSlots,
            kind: 'showalso',
            label: slotInfo.patientName + ' - ' + slotInfo.therapistName + ' - ' + slotInfo.therapyTypeName
          });
        });

        if (palestraTherapistId) {
          weekData.slots.forEach(function(slotInfo) {
            if (slotInfo.date !== colDateStr || slotInfo.therapistId !== palestraTherapistId) { return; }
            items.push({
              key: 'palestraown-' + slotInfo.id,
              startSlot: slotInfo.timeSlot,
              endSlot: slotInfo.timeSlot + slotInfo.durationSlots,
              kind: 'showalso',
              label: slotInfo.patientName + ' - ' + slotInfo.therapistName + ' - ' + slotInfo.therapyTypeName
            });
          });
        }

        columns.push({
          bgCells: bgCells,
          items: items,
          groups: computeOverlapGroups(items),
          isToday: colDateStr === todayStr
        });
      }

      var $table = $('<table class="scheduling-grid pianificazione-grid"></table>');
      var $thead = $('<thead></thead>');
      var $headerRow = $('<tr></tr>');
      $headerRow.append('<th>Orario</th>');

      weekDays.forEach(function(day, i) {
        var colDate = addDays(weekStart, i);
        var isToday = formatDateISO(colDate) === todayStr;
        var $th = $('<th' + (isToday ? ' class="pianificazione-today-header"' : '') + '>' + day.label + ' ' + formatDateDDMM(colDate) + '</th>');
        $headerRow.append($th);
      });

      $thead.append($headerRow);
      $table.append($thead);

      var $tbody = $('<tbody></tbody>');
      var tdRefs = [];
      for (var col0 = 0; col0 < 5; col0++) { tdRefs.push([]); }

      for (var row = 0; row < rowCount; row++) {
        var slotVal = gridMin + row;
        var $row = $('<tr></tr>');
        $row.append('<td class="scheduling-time-cell">' + slotToTime(slotVal) + '</td>');

        for (var col = 0; col < 5; col++) {
          var bgInfo = columns[col].bgCells[row];
          var todayClass = columns[col].isToday ? ' pianificazione-today-col' : '';
          var cellDateStr = formatDateISO(addDays(weekStart, col));
          var blockedClass = bgInfo.palestraBlocked ? ' pianificazione-mixed-unavailable' : '';

          var $td = $('<td class="pianificazione-reparto-cell ' + bgInfo.capacityClass + blockedClass + todayClass + '"></td>');

          if (bgInfo.existingCount > 0) {
            var $count = $('<span class="pianificazione-reparto-count">' + bgInfo.existingCount + '</span>');
            (function(sessions, cellDate) {
              $count.on('click', function(e) {
                e.stopPropagation();
                showMixedSlotPopup(cellDate, sessions);
              });
            })(bgInfo.existingSessions, addDays(weekStart, col));
            $td.append($count);
          }

          if (!bgInfo.palestraBlocked && heavyRemaining() > 0) {
            (function(clickDate, clickSlot) {
              $td.on('click', function() { placeAt(clickDate, clickSlot); });
            })(addDays(weekStart, col), slotVal);
          }

          attachDragAndDropHandlers($td, cellDateStr, slotVal, null);

          $row.append($td);
          tdRefs[col][row] = $td;
        }

        $tbody.append($row);
      }

      currentTdRefs = tdRefs;
      $table.append($tbody);

      for (var c2 = 0; c2 < 5; c2++) {
        var colDateStr2 = formatDateISO(addDays(weekStart, c2));

        columns[c2].items.forEach(function(item) {
          var rowIndex = item.startSlot - gridMin;
          if (rowIndex < 0 || rowIndex >= rowCount) { return; }

          var span = item.endSlot - item.startSlot;
          var groupInfo = columns[c2].groups[item.key];
          var groupSize = groupInfo.groupSize;
          var idx = groupInfo.indexInGroup;

          var $overlay;
          if (item.kind === 'showalso') {
            $overlay = $('<div class="pianificazione-session-overlay pianificazione-reparto-overlay pianificazione-showalso" title="' + item.label + '"><span class="pianificazione-cell-label">' + item.label + '</span></div>');
          } else {
            var kindClass = item.draftRef.kind === 'light' ? 'pianificazione-mixed-light-overlay' : 'pianificazione-mixed-heavy-overlay';
            var draftClass = item.kind === 'draft' ? 'pianificazione-draft' : 'pianificazione-conflict';
            $overlay = $('<div class="pianificazione-session-overlay pianificazione-reparto-overlay ' + kindClass + ' ' + draftClass + '" title="' + item.label + '"><span class="pianificazione-cell-label">' + item.label + '</span></div>');
          }

          $overlay.css({
            top: '0',
            height: (span * PIANIFICAZIONE_ROW_HEIGHT - 1) + 'px',
            left: 'calc((100% - 2em) * ' + idx + ' / ' + groupSize + ')',
            width: 'calc((100% - 2em) / ' + groupSize + ')'
          });

          if (item.draftRef) {
            var $removeBtn = $('<button type="button" class="availability-remove">×</button>');
            (function(draftRef) {
              $removeBtn.on('click', function(e) {
                e.stopPropagation();
                draft = draft.filter(function(dd) { return dd !== draftRef; });
                partsRemaining[draftRef.partId]++;
                recomputeSelfConflicts();
                render();
              });
            })(item.draftRef);
            $overlay.append($removeBtn);

            attachDragAndDropHandlers($overlay, colDateStr2, item.startSlot, item.draftRef);
          }

          var $startTd = tdRefs[c2][rowIndex];
          if ($startTd) { $startTd.append($overlay); }
        });
      }

      $wrapper.append($table);
      appendGridContent($wrapper, shouldFade);
      positionEdgeOverlays($table);
    }

    function currentPalestraTherapistName() {
      if (!therapistsInfo || !palestraTherapistId) { return ''; }
      var match = therapistsInfo.palestraTherapists.filter(function(t) { return t.id === palestraTherapistId; });
      return match.length ? match[0].name : '';
    }

    // --- Placement ---------------------------------------------------------

    function pushOccurrenceToDraft(occ) {
      occ.heavySegments.forEach(function(seg) {
        draft.push({
          kind: 'heavy',
          partId: seg.partId,
          therapyTypeId: seg.therapyTypeId,
          therapyTypeName: seg.therapyTypeName,
          durationSlots: seg.durationSlots,
          date: occ.date,
          timeSlot: seg.timeSlot,
          therapistId: seg.therapistId,
          therapistName: seg.therapistName,
          conflict: occ.conflict,
          _dragId: dragIdCounter++
        });
        partsRemaining[seg.partId]--;
      });
      occ.lightSegments.forEach(function(seg) {
        draft.push({
          kind: 'light',
          partId: seg.partId,
          therapyTypeId: seg.therapyTypeId,
          therapyTypeName: seg.therapyTypeName,
          durationSlots: seg.durationSlots,
          date: occ.date,
          timeSlot: seg.timeSlot,
          therapistId: null,
          therapistName: 'Reparto',
          conflict: occ.conflict,
          _dragId: dragIdCounter++
        });
        partsRemaining[seg.partId]--;
      });
    }

    function placeAt(dateObj, timeSlot) {
      var dateStr = formatDateISO(dateObj);

      if (mode !== 'singolo' && rimpiazza) {
        draft = [];
        resetPartsRemaining();
      }

      var remainingOverride = info.heavyParts.concat(info.lightParts).map(function(p) {
        return { partId: p.id, remaining: partsRemaining[p.id] };
      });

      $.ajax({
        url: 'Pianificazione/MixedAutoFill',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({
          therapyId: therapyId,
          palestraTherapistId: palestraTherapistId,
          repartoTherapistId: repartoTherapistId,
          mode: mode,
          startDate: dateStr,
          timeSlot: timeSlot,
          count: mode === 'singolo' ? 1 : 0,
          remainingOverride: remainingOverride
        })
      })
        .done(function(occurrences) {
          // TEMPORARY diagnostic - remove once the before/after picking issue is confirmed fixed.
          occurrences.forEach(function(occ) {
            if (occ.debug) {
              console.log('Mixed placement debug for ' + occ.date + ':', occ.debug);
            }
          });
          occurrences.forEach(pushOccurrenceToDraft);
          lastError = '';
          recomputeSelfConflicts();
          render();
        })
        .fail(function(jqXHR) {
          lastError = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Operazione non riuscita.';
          render();
        });
    }

    function onAccept() {
      var placementsByDate = {};

      draft.forEach(function(d) {
        if (!placementsByDate[d.date]) {
          placementsByDate[d.date] = { date: d.date, heavySegments: [], lightSegments: [] };
        }
        var seg = { partId: d.partId, timeSlot: d.timeSlot, therapistId: d.therapistId };
        if (d.kind === 'heavy') {
          placementsByDate[d.date].heavySegments.push(seg);
        } else {
          placementsByDate[d.date].lightSegments.push(seg);
        }
      });

      var placements = Object.keys(placementsByDate).map(function(date) { return placementsByDate[date]; });

      $.ajax({
        url: 'Pianificazione/MixedAccept',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({ therapyId: therapyId, placements: placements })
      })
        .done(function() {
          navigateTo('pazienti', info.patientId);
        })
        .fail(function(jqXHR) {
          lastError = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Salvataggio non riuscito.';
          renderGrid();
        });
    }

    setupEdgeOverlays(function(side) {
      navigateWeek(addDays(weekStart, side === 'left' ? -7 : 7));
    });

    $.get('Pianificazione/MixedInfo/' + therapyId).done(function(infoResult) {
      info = infoResult;
      resetPartsRemaining();

      $.get('Pianificazione/MixedTherapistsFor/' + therapyId).done(function(therapistsResult) {
        therapistsInfo = therapistsResult;
        render();
      });
    });
  }

  // --- Pazienti (Patients) ---------------------------------------------------

  var pazientiState = { filter: '', page: 1, sortBy: 'created', sortDir: 'desc' };
  var pazientiFilterTimeout = null;

  // Phone display: 3-digit lead group, then 2-digit groups, with the last group
  // flexing to 3 digits when needed so nothing is left dangling as a single digit
  // (e.g. "333 12 34 567" for 10 digits, "333 12 34 56" for 9).
  function formatPhoneDisplay(phone) {
    if (!phone) {
      return '';
    }

    var digits = phone.replace(/[^0-9]/g, '');
    if (digits.length <= 3) {
      return digits;
    }

    var groups = [];
    var rest = digits.slice(3);
    var i = 0;

    while (i < rest.length) {
      var remaining = rest.length - i;
      var take = remaining === 3 ? 3 : 2;
      groups.push(rest.slice(i, i + take));
      i += take;
    }

    return digits.slice(0, 3) + ' ' + groups.join(' ');
  }

  // Birthdate/creation-date display: strip the time portion via plain string
  // splitting rather than a Date object, so there's no timezone-shift risk at all.
  function formatSimpleDate(dateStr) {
    if (!dateStr) {
      return '';
    }

    var parts = dateStr.split('T')[0].split('-');
    return parts[2] + '/' + parts[1] + '/' + parts[0];
  }

  function renderPazientiList() {
    var $newButton = $('<button type="button">Crea paziente</button>');
    $newButton.on('click', function() {
      navigateTo('pazienti', 'new');
    });
    setTopbarActions(currentSession && currentSession.isAccettazione ? $newButton : null);

    var $wrapper = $('<div></div>');

    var $filterWrap = $('<div class="patients-filter-wrap"></div>');
    var $filterInput = $('<input type="text" class="patients-filter-input" placeholder="Cerca per nome o telefono (min. 3 caratteri)">').val(pazientiState.filter);
    var $filterClear = $('<button type="button" class="patients-filter-clear">×</button>');
    $filterWrap.append($filterInput).append($filterClear);
    $wrapper.append($filterWrap);

    var $table = $(
      '<table class="data-table"><thead><tr>' +
      '<th class="patients-sort-header" data-sort="name">Nome</th>' +
      '<th>Telefono</th>' +
      '<th>Data di nascita</th>' +
      '<th>Terapie</th>' +
      '<th class="patients-sort-header" data-sort="created">Data creazione</th>' +
      '</tr></thead><tbody></tbody></table>'
    );
    $wrapper.append($table);

    var $pagination = $('<div class="patients-pagination"></div>');
    $wrapper.append($pagination);

    $('#content').empty().append($wrapper);

    function updateSortIndicators() {
      $table.find('.patients-sort-header').removeClass('sort-asc sort-desc');
      $table.find('.patients-sort-header[data-sort="' + pazientiState.sortBy + '"]')
        .addClass(pazientiState.sortDir === 'asc' ? 'sort-asc' : 'sort-desc');
    }

    function renderPagination(result) {
      $pagination.empty();
      var totalPages = Math.max(1, Math.ceil(result.totalCount / result.pageSize));

      var $prev = $('<button type="button" class="secondary">‹ Precedente</button>');
      $prev.prop('disabled', pazientiState.page <= 1);
      $prev.on('click', function() {
        pazientiState.page -= 1;
        loadPazientiPage();
      });

      var $info = $('<span class="patients-page-info"></span>')
        .text('Pagina ' + pazientiState.page + ' di ' + totalPages + ' (' + result.totalCount + ' pazienti)');

      var $next = $('<button type="button" class="secondary">Successiva ›</button>');
      $next.prop('disabled', pazientiState.page >= totalPages);
      $next.on('click', function() {
        pazientiState.page += 1;
        loadPazientiPage();
      });

      $pagination.append($prev).append($info).append($next);
    }

    function loadPazientiPage() {
      $.get('Patients/List', {
        filter: pazientiState.filter,
        page: pazientiState.page,
        sortBy: pazientiState.sortBy,
        sortDir: pazientiState.sortDir
      }).done(function(result) {
        updateSortIndicators();

        var $tbody = $table.find('tbody');
        $tbody.empty();

        result.rows.forEach(function(p) {
          var $row = $('<tr></tr>');
          $row.append($('<td></td>').text(p.name));
          $row.append($('<td></td>').text(formatPhoneDisplay(p.phone)));
          $row.append($('<td></td>').text(p.isDuplicateName ? formatSimpleDate(p.dateOfBirth) : ''));
          $row.append($('<td></td>').text(p.therapyRecap || ''));
          $row.append($('<td></td>').text(formatSimpleDate(p.modDate)));

          $row.on('click', function() {
            navigateTo('pazienti', p.id);
          });

          $tbody.append($row);
        });

        renderPagination(result);
      });
    }

    $filterClear.on('click', function() {
      $filterInput.val('');
      pazientiState.filter = '';
      pazientiState.page = 1;
      loadPazientiPage();
    });

    $filterInput.on('input', function() {
      var val = $filterInput.val();
      clearTimeout(pazientiFilterTimeout);
      pazientiFilterTimeout = setTimeout(function() {
        if (val.length >= 3 || val.length === 0) {
          pazientiState.filter = val;
          pazientiState.page = 1;
          loadPazientiPage();
        }
      }, 300);
    });

    $table.find('.patients-sort-header').on('click', function() {
      var field = $(this).data('sort');

      if (pazientiState.sortBy === field) {
        pazientiState.sortDir = pazientiState.sortDir === 'asc' ? 'desc' : 'asc';
      } else {
        pazientiState.sortBy = field;
        pazientiState.sortDir = field === 'name' ? 'asc' : 'desc';
      }

      pazientiState.page = 1;
      loadPazientiPage();
    });

    loadPazientiPage();
  }

  // Splits on any non-letter (the Italian convention) and title-cases each resulting
  // run of letters, leaving every separator (space, apostrophe, hyphen...) untouched.
  function capitalizeItalianName(value) {
    return value.replace(/[a-zA-ZÀ-ÖØ-öø-ÿ]+/g, function(word) {
      return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase();
    });
  }

  function countNameWords(value) {
    return value.split(/[^a-zA-ZÀ-ÖØ-öø-ÿ]+/).filter(function(w) { return w.length > 0; }).length;
  }

  function renderPazienteForm(id) {
    var isEdit = id !== null;

    function buildForm(data) {
      var $form = $('<div class="form-box form-box-grid"></div>');
      var $leftCol = $('<div class="form-box-col"></div>');
      var $rightCol = $('<div class="form-box-col"></div>');

      $leftCol.append('<label for="patient-name">Nome</label>');
      var $nameInput = $('<input type="text" id="patient-name" value="' + (data ? data.name : '') + '">');
      $leftCol.append($nameInput);
      markRequired($nameInput);

      // Live auto-capitalization as typing occurs. Since it only ever changes case
      // (never length), the cursor position stays valid after reassigning .value.
      $nameInput.on('input', function() {
        var el = this;
        var cursorPos = el.selectionStart;
        var oldVal = el.value;
        var newVal = capitalizeItalianName(oldVal);

        if (newVal !== oldVal) {
          el.value = newVal;
          el.setSelectionRange(cursorPos, cursorPos);
        }
      });

      $rightCol.append('<label for="patient-phone">Telefono</label>');
      var $phoneInput = $('<input type="text" id="patient-phone" value="' + (data ? data.phone : '') + '">');
      $rightCol.append($phoneInput);
      markRequired($phoneInput);

      $leftCol.append('<label for="patient-dob">Data di nascita</label>');
      var dobVal = data && data.dateOfBirth ? data.dateOfBirth.split('T')[0] : '';
      $leftCol.append('<input type="date" id="patient-dob" value="' + dobVal + '">');

      $rightCol.append('<label for="patient-sex">Sesso</label>');
      var $sex = $(
        '<select id="patient-sex">' +
        '<option value="0">Maschio</option>' +
        '<option value="1">Femmina</option>' +
        '</select>'
      );
      $rightCol.append($sex);
      $sex.val(data ? String(data.sex) : '0');

      $leftCol.append('<label for="patient-document">Specialistica (PDF)</label>');
      var $documentInput = $('<input type="file" id="patient-document" accept="application/pdf,.pdf">');
      $leftCol.append($documentInput);

      if (isEdit && data && data.documentFileName) {
        var $docWrap = $('<div class="patient-document-wrap"></div>');
        var $docIcon = $('<img src="document-icon.png" class="patient-document-icon" alt="Documento" title="Apri specialistica">');
        var $docLink = $('<span class="patient-document-link">Apri specialistica</span>');
        var $docError = $('<span class="form-error patient-document-error"></span>');

        function openDocument() {
          $.get('Patients/DocumentStatus/' + id).done(function(status) {
            if (status.exists) {
              window.open('Patients/Document/' + id, '_blank');
            } else {
              $docError.text('Documento non più disponibile.').show();
            }
          });
        }

        $docIcon.on('click', openDocument);
        $docLink.on('click', openDocument);

        $docWrap.append($docIcon).append($docLink).append($docError);
        $rightCol.append($docWrap);
      }

      $form.append($leftCol).append($rightCol);
      $form.append('<div id="patient-error" class="form-error form-box-full-row"></div>');

      if (isEdit) {
        var $therapySection = $('<div class="patient-therapy-section form-box-full-row"></div>');
        $therapySection.append('<label>Terapia</label>');

        var $therapyDisplay = $('<div class="patient-therapy-display"></div>');
        var $therapyFormArea = $('<div class="patient-therapy-form-area"></div>');
        $therapyFormArea.hide();
        $therapySection.append($therapyDisplay).append($therapyFormArea);
        $form.append($therapySection);

        var therapyFormIsOpen = false;
        var getTherapyPayloadIfValid = null; // set inside openTherapyForm while it's open

        function renderTherapyDisplay() {
          therapyFormIsOpen = false;
          getTherapyPayloadIfValid = null;
          $therapyDisplay.empty().show();
          $therapyFormArea.empty().hide();

          var therapy = data.currentTherapy;

          if (therapy) {
            var $summary = $('<div class="therapy-summary"></div>');
            var summaryLabel = therapy.name ? (therapy.name + ' - ' + therapy.statusLabel) : therapy.statusLabel;
            $summary.append('<div class="therapy-summary-name">' + summaryLabel + '</div>');

            var $partsList = $('<ul class="therapy-parts-list"></ul>');
            therapy.parts.forEach(function(p) {
              $partsList.append('<li>' + p.sessionCount + ' × ' + (p.therapyTypeName || '?') + '</li>');
            });
            $summary.append($partsList);
            $therapyDisplay.append($summary);
          }

          var $buttonsRow = $('<div class="therapy-buttons-row"></div>');
          var isLocked = therapy && therapy.status !== 0; // 0 = TherapyStatus.ToBeScheduled

          // Pianifica terapia: single-part Palestra (canPlanPalestra) and all-parts-
          // Reparto-Light (canPlanReparto) keep their own simpler, dedicated flows.
          // Everything else that has at least one Heavy part (Palestra Active and/or
          // Reparto Active, alone or mixed with Reparto Light) goes through the Mixed
          // flow, which subsumes both single- and multi-part Heavy-only cases too.
          var canPlanPalestra = therapy &&
            therapy.parts.length === 1 &&
            therapy.parts[0].therapyTypeCategory === 1 &&
            therapy.parts[0].remaining > 0;

          var canPlanReparto = therapy &&
            therapy.parts.length > 0 &&
            therapy.parts.every(function(p) {
              return p.therapyTypeCategory === 0 && p.therapyTypeExecutionType === 0;
            }) &&
            therapy.parts.some(function(p) { return p.remaining > 0; });

          function isHeavyPart(p) {
            return p.therapyTypeCategory === 1 || (p.therapyTypeCategory === 0 && p.therapyTypeExecutionType === 1);
          }

          var canPlanMixed = !canPlanPalestra && !canPlanReparto && therapy &&
            therapy.parts.some(isHeavyPart) &&
            therapy.parts.some(function(p) { return p.remaining > 0; });

          if (canPlanPalestra) {
            var $planBtn = $('<button type="button">Pianifica terapia</button>');
            $planBtn.on('click', function() {
              navigateTo('pianificazione', therapy.parts[0].id);
            });
            $buttonsRow.append($planBtn);
          } else if (canPlanReparto) {
            var $planRepartoBtn = $('<button type="button">Pianifica terapia</button>');
            $planRepartoBtn.on('click', function() {
              navigateTo('pianificazioneReparto', therapy.id);
            });
            $buttonsRow.append($planRepartoBtn);
          } else if (canPlanMixed) {
            var $planMixedBtn = $('<button type="button">Pianifica terapia</button>');
            $planMixedBtn.on('click', function() {
              navigateTo('pianificazioneMixed', therapy.id);
            });
            $buttonsRow.append($planMixedBtn);
          }

          var editLabel = therapy ? 'Modifica terapia' : 'Aggiungi terapia';

          var $editBtn = $('<button type="button">' + editLabel + '</button>');
          $editBtn.prop('disabled', !!isLocked);
          $editBtn.on('click', function() {
            openTherapyForm(therapy);
          });
          $buttonsRow.append($editBtn);

          if (therapy) {
            var $removeBtn = $('<button type="button" class="danger">Rimuovi terapia</button>');
            $removeBtn.on('click', function() {
              showRemoveModal({
                endpoint: 'Therapies',
                id: therapy.id,
                name: data.name,
                onSuccess: function() {
                  navigateTo('pazienti', id);
                }
              });
            });
            $buttonsRow.append($removeBtn);
          }

          $therapyDisplay.append($buttonsRow);
        }

        function openTherapyForm(therapy) {
          loadTherapyTypes(function() {
            therapyFormIsOpen = true;
            $therapyDisplay.hide();
            $therapyFormArea.empty().show();

            $therapyFormArea.append('<label>Nome (opzionale)</label>');
            var $therapyNameInput = $('<input type="text" class="therapy-name-input">').val(therapy ? (therapy.name || '') : '');
            $therapyFormArea.append($therapyNameInput);

            var $partsContainer = $('<div class="therapy-parts-editor"></div>');
            $therapyFormArea.append($partsContainer);

            function addPartRow(therapyTypeId, sessionCount) {
              var $row = $('<div class="therapy-part-row"></div>');
              var $typeSelect = $('<select class="therapy-part-type"></select>');

              therapyTypesCache.forEach(function(t) {
                $typeSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
              });

              if (therapyTypeId) {
                $typeSelect.val(therapyTypeId);
              }

              var $sessionInput = $('<input type="number" class="therapy-part-sessions" min="1" value="' + (sessionCount || 10) + '">');

              var $removePartBtn = $('<button type="button" class="availability-remove">×</button>');
              $removePartBtn.on('click', function() {
                $row.remove();
              });

              $row.append($typeSelect).append($sessionInput).append($removePartBtn);
              $partsContainer.append($row);
            }

            if (therapy && therapy.parts.length > 0) {
              therapy.parts.forEach(function(p) {
                addPartRow(p.therapyTypeId, p.sessionCount);
              });
            } else {
              addPartRow(null, 10);
            }

            var $addPartBtn = $('<button type="button" class="availability-add-btn">+ Aggiungi un\'altra parte alla terapia</button>');
            $addPartBtn.on('click', function() {
              addPartRow(null, 10);
            });
            $therapyFormArea.append($addPartBtn);

            $therapyFormArea.append('<div class="form-error therapy-error"></div>');

            var $therapyActionsRow = $('<div class="therapy-actions-row"></div>');
            var $therapyCancelBtn = $('<button type="button" class="secondary">Annulla</button>');

            $therapyCancelBtn.on('click', function() {
              renderTherapyDisplay();
            });

            // Called from the page's single Salva button when this sub-form is open -
            // returns the payload if valid, or shows the inline error and returns null.
            // No separate "Salva terapia" button anymore (CPU: merge into one Save).
            getTherapyPayloadIfValid = function() {
              var parts = [];
              var valid = true;

              $partsContainer.find('.therapy-part-row').each(function() {
                var typeId = parseInt($(this).find('.therapy-part-type').val(), 10);
                var sessions = parseInt($(this).find('.therapy-part-sessions').val(), 10);

                if (!typeId || !sessions || sessions < 1) {
                  valid = false;
                }

                parts.push({ therapyTypeId: typeId, sessionCount: sessions });
              });

              if (!valid || parts.length === 0) {
                $therapyFormArea.find('.therapy-error').text('Serve almeno una parte valida, con almeno una seduta.').show();
                return null;
              }

              $therapyFormArea.find('.therapy-error').hide();

              return {
                patientId: id,
                name: $therapyNameInput.val(),
                parts: parts,
                existingTherapyId: therapy ? therapy.id : null
              };
            };

            $therapyActionsRow.append($therapyCancelBtn);
            $therapyFormArea.append($therapyActionsRow);
          });
        }

        renderTherapyDisplay();
      }

      $('#content').empty().append($form);

      // Therapists can see a patient's info and their therapies, but not modify
      // anything - grays out every field and hides every edit-capable control
      // already built into the form (document upload, per-therapy add/edit/
      // remove), while leaving the Terapie list itself fully visible (CPU's call).
      if (!currentSession || !currentSession.isAccettazione) {
        $form.find('input, select, textarea').prop('disabled', true);
        $form.find('button').hide();
      }

      var $save = $('<button type="button">Salva</button>');
      var $cancel = $('<button type="button" class="secondary">Cancella</button>');
      $cancel.on('click', function() {
        navigateTo('pazienti');
      });
      var dirty = trackDirty($form, $save, $cancel);

      function uploadDocumentIfSelected(patientId, callback) {
        var file = $documentInput[0].files[0];

        if (!file) {
          callback();
          return;
        }

        var formData = new FormData();
        formData.append('file', file);

        $.ajax({
          url: 'Patients/UploadDocument/' + patientId,
          method: 'POST',
          data: formData,
          processData: false,
          contentType: false
        })
          .done(function() {
            callback();
          })
          .fail(function(jqXHR) {
            var message = (jqXHR.responseJSON && jqXHR.responseJSON.message)
              ? jqXHR.responseJSON.message
              : 'Il caricamento del documento non è riuscito.';
            $('#patient-error').text('Paziente salvato, ma ' + message.charAt(0).toLowerCase() + message.slice(1)).show();
            callback();
          });
      }

      function doSave(payload, therapyPayload) {
        var request = isEdit
          ? $.ajax({ url: 'Patients/Update/' + id, method: 'PUT', contentType: 'application/json', data: JSON.stringify(payload) })
          : $.ajax({ url: 'Patients/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload) });

        request.done(function(response) {
          var patientId = isEdit ? id : response.id;

          uploadDocumentIfSelected(patientId, function() {
            if (!therapyPayload) {
              navigateTo('pazienti', patientId, !isEdit);
              return;
            }

            var therapyRequest = therapyPayload.existingTherapyId
              ? $.ajax({ url: 'Therapies/Update/' + therapyPayload.existingTherapyId, method: 'PUT', contentType: 'application/json', data: JSON.stringify(therapyPayload) })
              : $.ajax({ url: 'Therapies/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(therapyPayload) });

            therapyRequest.done(function() {
              navigateTo('pazienti', patientId, !isEdit);
            });

            therapyRequest.fail(function(jqXHR) {
              // Patient info is already saved at this point - only the therapy part
              // failed server-side (rare, since it was already validated client-side).
              // Stay on the page with the therapy form still open so nothing is lost.
              var message = (jqXHR.responseJSON && jqXHR.responseJSON.message)
                ? jqXHR.responseJSON.message
                : 'Salvataggio non riuscito.';
              $('#content').find('.therapy-error').text(message).show();
            });
          });
        });

        request.fail(function(jqXHR) {
          var message = (jqXHR.responseJSON && jqXHR.responseJSON.message)
            ? jqXHR.responseJSON.message
            : 'Il salvataggio non è riuscito. Controlla i dati inseriti.';
          $('#patient-error').text(message).show();
        });
      }

      // Same full name as another patient: pause and let the person confirm rather
      // than silently creating (or renaming into) a duplicate. Skipped entirely when
      // editing and the name hasn't actually changed.
      function checkDuplicateThenSave(payload, therapyPayload) {
        if (isEdit && data.name === payload.name) {
          doSave(payload, therapyPayload);
          return;
        }

        $.get('Patients/CheckDuplicateName', { name: payload.name, excludeId: isEdit ? id : null }).done(function(matches) {
          if (matches.length === 0) {
            doSave(payload, therapyPayload);
            return;
          }

          var listHtml = matches.map(function(m) {
            var dob = m.dateOfBirth ? formatSimpleDate(m.dateOfBirth) : 'n/d';
            return '<li>' + formatPhoneDisplay(m.phone) + ' - nato/a il ' + dob + '</li>';
          }).join('');

          window.showModal(
            '<p>Esiste già un paziente con il nome <strong>' + payload.name + '</strong>:</p>' +
            '<ul>' + listHtml + '</ul>' +
            '<p>Vuoi crearlo comunque?</p>',
            [
              {
                label: 'Crea comunque',
                className: 'danger',
                onClick: function() {
                  doSave(payload, therapyPayload);
                }
              },
              {
                label: 'Annulla',
                className: 'secondary',
                onClick: function() {}
              }
            ]
          );
        });
      }

      $save.on('click', function() {
        var name = capitalizeItalianName($nameInput.val());
        $nameInput.val(name);

        var payload = {
          name: name,
          phone: $('#patient-phone').val(),
          dateOfBirth: $('#patient-dob').val() || null,
          sex: parseInt($sex.val(), 10)
        };

        if (countNameWords(name) < 2) {
          $('#patient-error').text('Il Cognome e Nome del paziente sono obbligatori').show();
          return;
        }

        if (!payload.phone) {
          $('#patient-error').text('Il telefono è obbligatorio.').show();
          return;
        }

        // If the Therapy sub-form is open, it's part of this same Save - an invalid
        // sub-form blocks the whole save (patient info included), rather than saving
        // one and silently dropping the other (CPU: merge into a single Save action).
        var therapyPayload = null;

        if (therapyFormIsOpen && getTherapyPayloadIfValid) {
          therapyPayload = getTherapyPayloadIfValid();
          if (!therapyPayload) {
            return;
          }
        }

        $('#patient-error').hide();
        checkDuplicateThenSave(payload, therapyPayload);
      });

      var $actions = $('<div></div>');
      $actions.append($save).append($cancel);

      if (isEdit) {
        var $plan = $('<button type="button" class="secondary">Genera piano</button>');
        $plan.on('click', function() {
          navigateTo('piano', id);
        });
        $actions.append($plan);

        var $remove = $('<button type="button" class="danger">Rimuovi paziente</button>');
        $remove.on('click', function() {
          showRemoveModal({
            endpoint: 'Patients',
            id: id,
            name: data.name,
            onSuccess: function() {
              navigateTo('pazienti');
            }
          });
        });
        $actions.append($remove);
      }

      if (currentSession && currentSession.isAccettazione) {
        setTopbarActions($actions);
      } else {
        var $back = $('<button type="button" class="secondary">Indietro</button>');
        $back.on('click', function() { navigateTo('pazienti'); });
        setTopbarActions($back);
      }
    }

    if (isEdit) {
      $.get('Patients/Get/' + id).done(function(data) {
        buildForm(data);
      });
    } else {
      buildForm(null);
    }
  }

  // --- Utenti -------------------------------------------------------------

  function renderUtentiList() {
    $.get('Users/List', { includeRemoved: showRemoved, includeAudit: showAudit })
      .done(function(users) {
        var $newButton = $('<button type="button">Nuovo utente</button>');
        $newButton.on('click', function() {
          navigateTo('utenti', 'new');
        });
        setTopbarActions($newButton);

        var $wrapper = $('<div></div>');

        var $filter = $('<input type="text" class="filter-input" placeholder="Filtra per nome, telefono o ruolo...">');
        $wrapper.append($filter);

        var headerHtml = '<tr><th>Nome</th><th>Telefono</th><th>Ruolo</th>';
        if (showRemoved) {
          headerHtml += '<th>Rimosso</th>';
        }
        if (showAudit) {
          headerHtml += '<th>Modificatore</th><th>Data Modifica</th>';
        }
        headerHtml += '</tr>';

        var $table = $('<table class="data-table"><thead>' + headerHtml + '</thead><tbody></tbody></table>');
        var $tbody = $table.find('tbody');

        users.forEach(function(user) {
          var $row = $('<tr></tr>');

          if (!user.isActive) {
            $row.addClass('removed-row');
          }

          $row.data('name', user.name.toLowerCase());
          $row.data('phone', (user.phone || '').toLowerCase());
          $row.data('role', user.role.toLowerCase());

          $row.append($('<td></td>').text(user.name));
          $row.append($('<td></td>').text(formatPhoneDisplay(user.phone)));
          $row.append($('<td></td>').text(user.role));

          if (showRemoved) {
            $row.append($('<td></td>').html(user.isActive ? '' : '✓'));
          }

          if (showAudit) {
            $row.append($('<td></td>').text(user.modifier || ''));
            $row.append($('<td></td>').text(formatDate(user.modDate)));
          }

          $row.on('click', function() {
            navigateTo('utenti', user.id);
          });

          $tbody.append($row);
        });

        $wrapper.append($table);
        $('#content').empty().append($wrapper);

        $filter.on('input', function() {
          var term = $(this).val().toLowerCase();

          $tbody.find('tr').each(function() {
            var $row = $(this);
            var matches =
              $row.data('name').indexOf(term) !== -1 ||
              $row.data('phone').indexOf(term) !== -1 ||
              $row.data('role').indexOf(term) !== -1;

            $row.toggle(matches);
          });
        });
      });
  }

  function renderUtentiForm(id) {
    var isEdit = id !== null;

    function buildForm(data) {
      var readOnly = data ? !data.isActive : false;

      var $form = $('<div class="form-box form-box-wide form-box-grid"></div>');

      if (readOnly) {
        $form.append('<div class="readonly-banner form-box-full-row">Elemento rimosso — sola lettura.</div>');
      }

      var $leftCol = $('<div class="form-box-col"></div>');
      var $rightCol = $('<div class="form-box-col"></div>');

      $leftCol.append('<label for="user-name">Nome</label>');
      var $nameInput = $('<input type="text" id="user-name" autocomplete="off" value="' + (data ? data.name : '') + '">');
      $leftCol.append($nameInput);
      markRequired($nameInput);

      $rightCol.append('<label for="user-phone">Telefono</label>');
      $rightCol.append('<input type="text" id="user-phone" autocomplete="off" value="' + (data ? data.phone : '') + '">');

      $leftCol.append('<label for="user-password">Password</label>');
      var passwordPlaceholder = isEdit ? 'Lascia vuoto per non modificare' : '';
      $leftCol.append('<input type="password" id="user-password" autocomplete="new-password" placeholder="' + passwordPlaceholder + '">');

      $rightCol.append('<label for="user-password-confirm">Conferma password</label>');
      $rightCol.append('<input type="password" id="user-password-confirm" autocomplete="new-password" placeholder="' + passwordPlaceholder + '">');

      $leftCol.append('<label for="user-role">Ruolo</label>');
      var $role = $(
        '<select id="user-role">' +
        '<option value="accettazione">Accettazione</option>' +
        '<option value="terapista">Terapista</option>' +
        '</select>'
      );
      $leftCol.append($role);

      $rightCol.append('<label for="user-operating-area">Operatività</label>');
      var $operatingArea = $(
        '<select id="user-operating-area">' +
        '<option value="0">Palestra</option>' +
        '<option value="2">Reparto</option>' +
        '<option value="4">Palestra e aiuto a reparto</option>' +
        '<option value="6">Reparto e aiuto a palestra</option>' +
        '</select>'
      );
      $rightCol.append($operatingArea);

      $form.append($leftCol).append($rightCol);

      // Straordinario: last of the "fields" proper, own full-width row.
      var $overtimeRow = $('<div class="checkbox-row form-box-full-row"></div>');
      var $overtime = $('<input type="checkbox" id="user-overtime">');
      $overtimeRow.append($overtime);
      $overtimeRow.append('<label for="user-overtime" style="margin:0;">Straordinari consentiti</label>');
      $form.append($overtimeRow);

      $form.append('<div id="user-password-error" class="form-error form-box-full-row"></div>');

      // Weekly availability (Terapista-only): 5 weekday columns, each with a "+" to add
      // a 15-minute-granularity slot and a red X on every slot to remove it.
      // Not available on the create form: TherapistAvailability needs a real TherapistId,
      // which doesn't exist until the user is actually saved.
      var $availabilitySection = $('<div class="availability-section form-box-full-row"></div>');
      $availabilitySection.append('<label>Disponibilità settimanale</label>');
      var $availabilityColumns = null;
      var dayColumns = {};

      // Time box: shows the current value, click opens the scrollable popup picker.
      function buildTimeBox(className, slot) {
        var $box = $('<button type="button" class="availability-time-box ' + className + '"></button>');
        $box.data('slot', slot);
        $box.text(slotToTime(slot));

        $box.on('click', function(e) {
          e.stopPropagation();
          openTimePicker($box, $box.data('slot'), function(newSlot) {
            $box.data('slot', newSlot);
            $box.text(slotToTime(newSlot));
            if (dirty) {
              dirty.refresh();
            }
          });
        });

        return $box;
      }

      function addAvailabilitySlot($slotList, startTime, endTime) {
        var startSlot = (startTime !== null && startTime !== undefined) ? startTime : timeToSlot('09:00');
        var endSlot = (endTime !== null && endTime !== undefined) ? endTime : timeToSlot('13:00');

        var $slot = $('<div class="availability-slot"></div>');
        var $times = $('<div class="availability-slot-times"></div>');
        var $start = buildTimeBox('availability-start', startSlot);
        var $end = buildTimeBox('availability-end', endSlot);

        var $remove = $('<button type="button" class="availability-remove">×</button>');

        $remove.on('click', function() {
          $slot.remove();
          if (dirty) {
            dirty.refresh();
          }
        });

        $times.append($start).append($end);
        $slot.append($times).append($remove);
        $slotList.append($slot);
      }

      if (isEdit) {
        ensureAvailabilityRange(function() {}); // warm the cache before the user opens a picker
        $availabilityColumns = $('<div class="availability-columns"></div>');

        weekDays.forEach(function(day) {
          var $col = $('<div class="availability-column" data-day="' + day.value + '"></div>');
          $col.append('<div class="availability-column-header">' + day.label + '</div>');
          var $slotList = $('<div class="availability-slot-list"></div>');
          $col.append($slotList);

          var $addBtn = $('<button type="button" class="availability-add-btn">+</button>');
          $addBtn.on('click', function() {
            addAvailabilitySlot($slotList, null, null);
            if (dirty) {
              dirty.refresh();
            }
          });
          $col.append($addBtn);

          dayColumns[day.value] = $slotList;
          $availabilityColumns.append($col);
        });

        $availabilitySection.append($availabilityColumns);

        if (data && data.availability) {
          data.availability.forEach(function(slot) {
            var $slotList = dayColumns[slot.dayOfWeek];
            if ($slotList) {
              addAvailabilitySlot($slotList, slot.startTime, slot.endTime);
            }
          });
        }
      } else {
        $availabilitySection.append('<div class="availability-not-yet-note">Salva l\'utente per impostare la disponibilità settimanale.</div>');
      }

      $form.append($availabilitySection);
      $form.append('<div id="user-availability-error" class="form-error form-box-full-row"></div>');

      var initialArea = data ? data.operatingArea : 0;
      var initialIsAccettazione = (initialArea & 1) !== 0;

      $role.val(initialIsAccettazione ? 'accettazione' : 'terapista');
      $operatingArea.val(initialIsAccettazione ? '0' : String(initialArea));
      $overtime.prop('checked', data ? data.overtimeAllowed === 1 : false);

      function refreshTherapistFieldsVisibility() {
        var isTerapista = $role.val() === 'terapista';
        var $areaLabel = $form.find('label[for="user-operating-area"]');
        var $availabilityLabel = $form.find('label').filter(function() {
          return $(this).text() === 'Disponibilità settimanale';
        });

        if (isTerapista) {
          $areaLabel.show();
          $operatingArea.show();
          $overtimeRow.show();
          $availabilityLabel.show();
          if ($availabilityColumns) {
            $availabilityColumns.show();
          }
          $form.find('.availability-not-yet-note').show();
        } else {
          $areaLabel.hide();
          $operatingArea.hide();
          $overtimeRow.hide();
          $availabilityLabel.hide();
          if ($availabilityColumns) {
            $availabilityColumns.hide();
          }
          $form.find('.availability-not-yet-note').hide();
        }
      }

      $role.on('change', refreshTherapistFieldsVisibility);
      refreshTherapistFieldsVisibility();

      if (readOnly) {
        $form.find('input, select, button').prop('disabled', true);
      }

      $('#content').empty().append($form);

      var $actions = $('<div></div>');

      if (readOnly) {
        var $cancelOnly = $('<button type="button" class="secondary">Chiudi</button>');
        $cancelOnly.on('click', function() {
          navigateTo('utenti');
        });
        $actions.append($cancelOnly);
        setTopbarActions($actions);
        return;
      }

      var $save = $('<button type="button">Salva</button>');
      var $cancel = $('<button type="button" class="secondary">Cancella</button>');
      $cancel.on('click', function() {
        navigateTo('utenti');
      });
      var dirty = trackDirty($form, $save, $cancel);

      $save.on('click', function() {
        var password = $('#user-password').val();
        var passwordConfirm = $('#user-password-confirm').val();

        if (!$('#user-name').val()) {
          $('#user-password-error').text('Il nome è obbligatorio.').show();
          return;
        }

        if (!isEdit && password === '') {
          $('#user-password-error').text('La password è obbligatoria.').show();
          return;
        }

        if (password !== passwordConfirm) {
          $('#user-password-error').text('Le password non coincidono.').show();
          return;
        }

        $('#user-password-error').hide();

        var role = $role.val();
        var operatingArea = role === 'accettazione' ? 1 : parseInt($operatingArea.val(), 10);
        var overtimeAllowed = (role === 'terapista' && $overtime.is(':checked')) ? 1 : 0;

        var availability = [];
        if (role === 'terapista' && $availabilityColumns) {
          $availabilityColumns.find('.availability-column').each(function() {
            var day = parseInt($(this).data('day'), 10);
            $(this).find('.availability-slot').each(function() {
              availability.push({
                dayOfWeek: day,
                startTime: $(this).find('.availability-start').data('slot'),
                endTime: $(this).find('.availability-end').data('slot')
              });
            });
          });
        }

        var availabilityError = validateAvailability(availability);
        if (availabilityError) {
          $('#user-availability-error').text(availabilityError).show();
          return;
        }

        $('#user-availability-error').hide();

        var payload = {
          name: $('#user-name').val(),
          phone: $('#user-phone').val(),
          operatingArea: operatingArea,
          overtimeAllowed: overtimeAllowed,
          password: password,
          availability: availability
        };

        var request = isEdit
          ? $.ajax({ url: 'Users/Update/' + id, method: 'PUT', contentType: 'application/json', data: JSON.stringify(payload) })
          : $.ajax({ url: 'Users/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload) });

        // Stay in edit mode after saving: re-navigate to the (now known) id rather than
        // going back to the list, refetching the just-saved data fresh from the server.
        // On create, replace the transient "new" history entry so Back lands on the list
        // in one press instead of bouncing through an empty "new" form.
        request.done(function(response) {
          schedulingTherapists = null; // name/role/availability may have changed
          navigateTo('utenti', isEdit ? id : response.id, !isEdit);
        });
      });

      $actions.append($save).append($cancel);

      if (isEdit) {
        var $remove = $('<button type="button" class="danger">Rimuovi terapista</button>');
        $remove.on('click', function() {
          showRemoveModal({
            endpoint: 'Users',
            id: id,
            name: data.name,
            onSuccess: function() {
              schedulingTherapists = null;
              navigateTo('utenti');
            }
          });
        });
        $actions.append($remove);
      }

      setTopbarActions($actions);
    }

    if (isEdit) {
      $.get('Users/Get/' + id).done(function(data) {
        buildForm(data);
      });
    } else {
      buildForm(null);
    }
  }

  // --- Terapie (TherapyTypes) ----------------------------------------------

  function renderTerapieList() {
    $.get('TherapyTypes/List', { includeRemoved: showRemoved, includeAudit: showAudit })
      .done(function(types) {
        var $newButton = $('<button type="button">Nuova terapia</button>');
        $newButton.on('click', function() {
          navigateTo('terapie', 'new');
        });
        setTopbarActions($newButton);

        var $wrapper = $('<div></div>');

        var $filter = $('<input type="text" class="filter-input" placeholder="Filtra per nome, categoria, tipo...">');
        $wrapper.append($filter);

        var headerHtml =
          '<tr><th>Nome</th><th>Abbreviazione</th><th>Durata</th><th>Categoria</th><th>Tipo</th><th>Colore</th>' +
          '<th>Max Maschi in Parallelo</th><th>Max Femmine in Parallelo</th><th>Frequenza settimanale</th>';
        if (showRemoved) {
          headerHtml += '<th>Rimosso</th>';
        }
        if (showAudit) {
          headerHtml += '<th>Modificatore</th><th>Data Modifica</th>';
        }
        headerHtml += '</tr>';

        var $table = $('<table class="data-table"><thead>' + headerHtml + '</thead><tbody></tbody></table>');
        var $tbody = $table.find('tbody');

        types.forEach(function(t) {
          var $row = $('<tr></tr>');

          if (!t.isActive) {
            $row.addClass('removed-row');
          }

          $row.data('name', t.name.toLowerCase());
          $row.data('category', t.category.toLowerCase());
          $row.data('type', t.type.toLowerCase());

          $row.append($('<td></td>').text(t.name));
          $row.append($('<td></td>').text(t.abbreviazione || ''));
          $row.append($('<td></td>').text(t.duration + ' min'));
          $row.append($('<td></td>').text(t.category));
          $row.append($('<td></td>').text(t.type));

          var $colorCell = $('<td></td>');
          $('<span></span>').css({
            display: 'inline-block',
            width: '18px',
            height: '18px',
            borderRadius: '4px',
            background: intToHexColor(t.color)
          }).appendTo($colorCell);
          $row.append($colorCell);

          $row.append($('<td></td>').text(t.maxParallelMale === null ? '-' : t.maxParallelMale));
          $row.append($('<td></td>').text(t.maxParallelFemale === null ? '-' : t.maxParallelFemale));
          $row.append($('<td></td>').text(t.therapyWeeklyFrequency));

          if (showRemoved) {
            $row.append($('<td></td>').html(t.isActive ? '' : '✓'));
          }

          if (showAudit) {
            $row.append($('<td></td>').text(t.modifier || ''));
            $row.append($('<td></td>').text(formatDate(t.modDate)));
          }

          $row.on('click', function() {
            navigateTo('terapie', t.id);
          });

          $tbody.append($row);
        });

        $wrapper.append($table);
        $('#content').empty().append($wrapper);

        $filter.on('input', function() {
          var term = $(this).val().toLowerCase();

          $tbody.find('tr').each(function() {
            var $row = $(this);
            var matches =
              $row.data('name').indexOf(term) !== -1 ||
              $row.data('category').indexOf(term) !== -1 ||
              $row.data('type').indexOf(term) !== -1;

            $row.toggle(matches);
          });
        });
      });
  }

  function renderTerapiaForm(id) {
    var isEdit = id !== null;

    function buildForm(data) {
      var readOnly = data ? !data.isActive : false;

      var $form = $('<div class="form-box"></div>');

      if (readOnly) {
        $form.append('<div class="readonly-banner">Elemento rimosso — sola lettura.</div>');
      }

      $form.append('<label for="type-name">Nome</label>');
      $form.append('<input type="text" id="type-name" value="' + (data ? data.name : '') + '">');

      $form.append('<label for="type-abbreviazione">Abbreviazione</label>');
      $form.append('<input type="text" id="type-abbreviazione" maxlength="8" value="' + (data && data.abbreviazione ? data.abbreviazione : '') + '">');

      $form.append('<label for="type-duration">Durata (minuti)</label>');
      $form.append('<input type="number" id="type-duration" value="' + (data ? data.duration : 15) + '">');

      $form.append('<label for="type-category">Categoria</label>');
      var $category = $(
        '<select id="type-category">' +
        '<option value="0">Reparto</option>' +
        '<option value="1">Palestra</option>' +
        '</select>'
      );
      $form.append($category);

      $form.append('<label for="type-execution">Tipo</label>');
      var $execution = $(
        '<select id="type-execution">' +
        '<option value="0">Senza operatore</option>' +
        '<option value="1">Con operatore</option>' +
        '</select>'
      );
      $form.append($execution);

      $form.append('<label for="type-color">Colore</label>');
      var $color = $('<input type="color" id="type-color">');
      $form.append($color);

      $form.append('<label for="type-max-male">Max Maschi in Parallelo</label>');
      $form.append('<input type="number" id="type-max-male" value="' + (data && data.maxParallelMale !== null ? data.maxParallelMale : '') + '">');

      $form.append('<label for="type-max-female">Max Femmine in Parallelo</label>');
      $form.append('<input type="number" id="type-max-female" value="' + (data && data.maxParallelFemale !== null ? data.maxParallelFemale : '') + '">');

      $form.append('<label for="type-frequency">Frequenza settimanale</label>');
      $form.append('<input type="number" id="type-frequency" value="' + (data ? data.therapyWeeklyFrequency : 7) + '">');

      $category.val(data ? String(data.category) : '0');
      $execution.val(data ? String(data.type) : '0');
      $color.val(data ? intToHexColor(data.color) : '#266e5a');

      if (readOnly) {
        $form.find('input, select').prop('disabled', true);
      }

      $('#content').empty().append($form);

      var $actions = $('<div></div>');

      if (readOnly) {
        var $cancelOnly = $('<button type="button" class="secondary">Chiudi</button>');
        $cancelOnly.on('click', function() {
          navigateTo('terapie');
        });
        $actions.append($cancelOnly);
        setTopbarActions($actions);
        return;
      }

      var $save = $('<button type="button">Salva</button>');
      var $cancel = $('<button type="button" class="secondary">Cancella</button>');
      $cancel.on('click', function() {
        navigateTo('terapie');
      });
      var dirty = trackDirty($form, $save, $cancel);

      $save.on('click', function() {
        var maxMaleVal = $('#type-max-male').val();
        var maxFemaleVal = $('#type-max-female').val();

        var payload = {
          name: $('#type-name').val(),
          abbreviazione: $('#type-abbreviazione').val() || null,
          duration: parseInt($('#type-duration').val(), 10),
          category: parseInt($category.val(), 10),
          type: parseInt($execution.val(), 10),
          color: hexColorToInt($color.val()),
          maxParallelMale: maxMaleVal === '' ? null : parseInt(maxMaleVal, 10),
          maxParallelFemale: maxFemaleVal === '' ? null : parseInt(maxFemaleVal, 10),
          therapyWeeklyFrequency: parseInt($('#type-frequency').val(), 10)
        };

        var request = isEdit
          ? $.ajax({ url: 'TherapyTypes/Update/' + id, method: 'PUT', contentType: 'application/json', data: JSON.stringify(payload) })
          : $.ajax({ url: 'TherapyTypes/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload) });

        // Stay in edit mode after saving: re-navigate to the (now known) id rather than
        // going back to the list, refetching the just-saved data fresh from the server.
        // On create, replace the transient "new" history entry so Back lands on the list
        // in one press instead of bouncing through an empty "new" form.
        request.done(function(response) {
          navigateTo('terapie', isEdit ? id : response.id, !isEdit);
        });
      });

      $actions.append($save).append($cancel);

      if (isEdit) {
        var $remove = $('<button type="button" class="danger">Rimuovi terapia</button>');
        $remove.on('click', function() {
          showRemoveModal({
            endpoint: 'TherapyTypes',
            id: id,
            name: data.name,
            onSuccess: function() {
              navigateTo('terapie');
            }
          });
        });
        $actions.append($remove);
      }

      setTopbarActions($actions);
    }

    if (isEdit) {
      $.get('TherapyTypes/Get/' + id).done(function(data) {
        buildForm(data);
      });
    } else {
      buildForm(null);
    }
  }

  // --- Assenze (Vacations: Assenze terapista + Festività) -------------------

  // "yyyy-MM-dd" -> local Date, built from the numeric parts directly (not via the
  // Date constructor's UTC string parsing) to avoid any timezone-shift surprises.
  function parseDateOnly(str) {
    var parts = str.split('-');
    return new Date(parseInt(parts[0], 10), parseInt(parts[1], 10) - 1, parseInt(parts[2], 10));
  }

  function formatVacationDate(dateStr) {
    if (!dateStr) {
      return '';
    }

    var d = parseDateOnly(dateStr);
    var dd = (d.getDate() < 10 ? '0' : '') + d.getDate();
    var mm = (d.getMonth() + 1 < 10 ? '0' : '') + (d.getMonth() + 1);
    return dayFullNames[d.getDay()] + ' ' + dd + '/' + mm + '/' + d.getFullYear();
  }

  function getSchedulingTherapistName(id) {
    if (!schedulingTherapists) {
      return '';
    }

    var match = schedulingTherapists.filter(function(t) {
      return t.id === id;
    });

    return match.length ? match[0].name : '';
  }

  function renderAssenzeList() {
    $.get('Vacations/List').done(function(rows) {
      var $newButton = $('<button type="button">Crea assenza</button>');
      $newButton.on('click', function() {
        navigateTo('assenze', 'new');
      });
      setTopbarActions($newButton);

      var $table = $(
        '<table class="data-table"><thead><tr>' +
        '<th>Tipo</th><th>Cosa</th><th>Data</th><th>Dettaglio</th>' +
        '</tr></thead><tbody></tbody></table>'
      );
      var $tbody = $table.find('tbody');

      rows.forEach(function(v) {
        var isFestivita = !v.therapistId;
        var tipo = isFestivita ? 'Festività' : 'Assenza';
        var nomeOrTerapista = isFestivita ? v.name : v.therapistName;

        var dataText;
        if (v.startDate) {
          dataText = v.startDate === v.endDate
            ? formatVacationDate(v.startDate)
            : formatVacationDate(v.startDate) + ' - ' + formatVacationDate(v.endDate);
        } else {
          dataText = formatVacationDate(v.sortDate);
        }

        var dettaglio = '';
        if (!isFestivita) {
          var fascia = v.ampm === 0 ? 'Mattina' : (v.ampm === 1 ? 'Pomeriggio' : 'Giornata intera');
          dettaglio = fascia;
          if (v.malattia === 1) {
            dettaglio += ' - Malattia';
          }
        }

        var $row = $('<tr></tr>');
        $row.append($('<td></td>').text(tipo));
        $row.append($('<td></td>').text(nomeOrTerapista || ''));
        $row.append($('<td></td>').text(dataText));
        $row.append($('<td></td>').text(dettaglio));

        $row.on('click', function() {
          navigateTo('assenze', v.id);
        });

        $tbody.append($row);
      });

      $('#content').empty().append($table);
    });
  }

  function renderAssenzaForm(id) {
    var isEdit = id !== null;

    function buildForm(data) {
      var isSeeded = data ? data.isSeeded === 1 : false;

      var $form = $('<div class="form-box"></div>');

      if (isSeeded) {
        $form.append('<div class="readonly-banner">Festività predefinita - sola lettura.</div>');
      }

      $form.append('<label for="vacation-tipo">Tipo</label>');
      var $tipo = $(
        '<select id="vacation-tipo">' +
        '<option value="assenza">Assenza terapista</option>' +
        '<option value="festivitaFissa">Festività fissa</option>' +
        '<option value="festivitaVariabile">Festività variabile / Chiusura</option>' +
        '</select>'
      );
      $form.append($tipo);

      // Nome: shared by both Festività kinds, hidden for Assenza.
      var $nameGroup = $('<div class="vacation-field-group"></div>');
      $nameGroup.append('<label for="vacation-name">Nome</label>');
      var $nameInput = $('<input type="text" id="vacation-name">');
      $nameGroup.append($nameInput);
      markRequired($nameInput);
      $form.append($nameGroup);

      // Assenza terapista fields
      var $assenzaGroup = $('<div class="vacation-field-group"></div>');
      $assenzaGroup.append('<label for="vacation-therapist">Terapista</label>');
      var $therapistSelect = $('<select id="vacation-therapist"><option value="">-- Seleziona terapista --</option></select>');
      schedulingTherapists.forEach(function(t) {
        $therapistSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
      });
      $assenzaGroup.append($therapistSelect);
      markRequired($therapistSelect);

      $assenzaGroup.append('<label for="vacation-assenza-start-date">Data inizio</label>');
      var $assenzaStartDateInput = $('<input type="date" id="vacation-assenza-start-date">');
      $assenzaGroup.append($assenzaStartDateInput);
      markRequired($assenzaStartDateInput);

      $assenzaGroup.append('<label for="vacation-assenza-end-date">Data fine</label>');
      var $assenzaEndDateInput = $('<input type="date" id="vacation-assenza-end-date">');
      $assenzaGroup.append($assenzaEndDateInput);

      var $fasciaLabel = $('<label for="vacation-fascia">Fascia</label>');
      $assenzaGroup.append($fasciaLabel);
      var $fasciaSelect = $(
        '<select id="vacation-fascia">' +
        '<option value="">Giornata intera</option>' +
        '<option value="0">Mattina</option>' +
        '<option value="1">Pomeriggio</option>' +
        '</select>'
      );
      $assenzaGroup.append($fasciaSelect);

      // Fascia (half-day) only makes sense for a genuine single day - hidden and
      // reset to "Giornata intera" as soon as the range spans more than one day.
      function refreshFasciaVisibility() {
        var isSingleDay = $assenzaStartDateInput.val() &&
          ($assenzaEndDateInput.val() === '' || $assenzaEndDateInput.val() === $assenzaStartDateInput.val());

        if (isSingleDay) {
          $fasciaLabel.show();
          $fasciaSelect.show();
        } else {
          $fasciaSelect.val('');
          $fasciaLabel.hide();
          $fasciaSelect.hide();
        }
      }

      $assenzaStartDateInput.on('change', refreshFasciaVisibility);
      $assenzaEndDateInput.on('change', refreshFasciaVisibility);

      var $malattiaRow = $('<div class="checkbox-row"></div>');
      var $malattia = $('<input type="checkbox" id="vacation-malattia">');
      $malattiaRow.append($malattia);
      $malattiaRow.append('<label for="vacation-malattia" style="margin:0;">Malattia</label>');
      $assenzaGroup.append($malattiaRow);

      $form.append($assenzaGroup);

      // Festività fissa: day + month, no year
      var $fissaGroup = $('<div class="vacation-field-group"></div>');
      $fissaGroup.append('<label>Giorno e mese</label>');
      var $monthDayRow = $('<div class="vacation-monthday-row"></div>');
      var $daySelect = $('<select id="vacation-day"></select>');
      for (var d = 1; d <= 31; d++) {
        $daySelect.append('<option value="' + d + '">' + d + '</option>');
      }
      var $monthSelect = $('<select id="vacation-month"></select>');
      monthNamesFull.forEach(function(name, i) {
        $monthSelect.append('<option value="' + (i + 1) + '">' + name + '</option>');
      });
      $monthDayRow.append($daySelect).append($monthSelect);
      $fissaGroup.append($monthDayRow);
      $form.append($fissaGroup);

      // Festività variabile: explicit date range
      var $variabileGroup = $('<div class="vacation-field-group"></div>');
      $variabileGroup.append('<label for="vacation-start-date">Data inizio</label>');
      var $startDateInput = $('<input type="date" id="vacation-start-date">');
      $variabileGroup.append($startDateInput);

      $variabileGroup.append('<label for="vacation-end-date">Data fine</label>');
      var $endDateInput = $('<input type="date" id="vacation-end-date">');
      $variabileGroup.append($endDateInput);
      $form.append($variabileGroup);

      $form.append('<div id="vacation-error" class="form-error"></div>');

      function refreshTipoVisibility() {
        $form.find('.vacation-field-group').hide();

        var tipo = $tipo.val();

        if (tipo === 'assenza') {
          $assenzaGroup.show();
        } else if (tipo === 'festivitaFissa') {
          $nameGroup.show();
          $fissaGroup.show();
        } else if (tipo === 'festivitaVariabile') {
          $nameGroup.show();
          $variabileGroup.show();
        }
      }

      $tipo.on('change', refreshTipoVisibility);

      // Prefill
      var initialTipo = 'assenza';
      if (data) {
        if (data.therapistId) {
          initialTipo = 'assenza';
        } else if (data.isYearIndependent === 1) {
          initialTipo = 'festivitaFissa';
        } else if (data.startDate) {
          initialTipo = 'festivitaVariabile';
        }
      }
      $tipo.val(initialTipo);

      if (data) {
        if (data.therapistId) {
          $therapistSelect.val(data.therapistId);
        }
        if (initialTipo === 'assenza') {
          if (data.startDate) {
            $assenzaStartDateInput.val(data.startDate);
          }
          if (data.endDate) {
            $assenzaEndDateInput.val(data.endDate);
          }
        } else if (data.startDate) {
          $startDateInput.val(data.startDate);
          if (data.endDate) {
            $endDateInput.val(data.endDate);
          }
        }
        $fasciaSelect.val(data.ampm === 0 || data.ampm === 1 ? String(data.ampm) : '');
        $malattia.prop('checked', data.malattia === 1);
        $nameInput.val(data.name || '');
        if (data.day) {
          $daySelect.val(String(data.day));
        }
        if (data.month) {
          $monthSelect.val(String(data.month));
        }
      }

      refreshFasciaVisibility();
      refreshTipoVisibility();

      if (isSeeded) {
        $form.find('input, select, button').prop('disabled', true);
      }

      $('#content').empty().append($form);

      var $actions = $('<div></div>');

      if (isSeeded) {
        var $cancelOnly = $('<button type="button" class="secondary">Chiudi</button>');
        $cancelOnly.on('click', function() {
          navigateTo('assenze');
        });
        $actions.append($cancelOnly);
        setTopbarActions($actions);
        return;
      }

      var $save = $('<button type="button">Salva</button>');
      var $cancel = $('<button type="button" class="secondary">Cancella</button>');
      $cancel.on('click', function() {
        navigateTo('assenze');
      });
      var dirty = trackDirty($form, $save, $cancel);

      $save.on('click', function() {
        var tipo = $tipo.val();
        var payload = { tipo: tipo };

        if (tipo === 'assenza') {
          var therapistIdVal = $therapistSelect.val();
          payload.therapistId = therapistIdVal ? parseInt(therapistIdVal, 10) : null;
          payload.startDate = $assenzaStartDateInput.val();
          payload.endDate = $assenzaEndDateInput.val() || null;
          payload.ampm = $fasciaSelect.val() === '' ? null : parseInt($fasciaSelect.val(), 10);
          payload.malattia = $malattia.is(':checked') ? 1 : 0;

          if (!payload.therapistId || !payload.startDate) {
            $('#vacation-error').text('Terapista e data inizio sono obbligatori.').show();
            return;
          }

          if (payload.endDate && payload.endDate < payload.startDate) {
            $('#vacation-error').text('La data fine deve essere successiva o uguale alla data inizio.').show();
            return;
          }
        } else if (tipo === 'festivitaFissa') {
          payload.name = $nameInput.val();
          payload.day = parseInt($daySelect.val(), 10);
          payload.month = parseInt($monthSelect.val(), 10);

          if (!payload.name) {
            $('#vacation-error').text('Il nome è obbligatorio.').show();
            return;
          }
        } else if (tipo === 'festivitaVariabile') {
          payload.name = $nameInput.val();
          payload.startDate = $startDateInput.val();
          payload.endDate = $endDateInput.val();

          if (!payload.name || !payload.startDate || !payload.endDate) {
            $('#vacation-error').text('Nome, data inizio e data fine sono obbligatori.').show();
            return;
          }

          if (payload.endDate < payload.startDate) {
            $('#vacation-error').text('La data fine deve essere successiva o uguale alla data inizio.').show();
            return;
          }
        }

        $('#vacation-error').hide();

        var request = isEdit
          ? $.ajax({ url: 'Vacations/Update/' + id, method: 'PUT', contentType: 'application/json', data: JSON.stringify(payload) })
          : $.ajax({ url: 'Vacations/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload) });

        request.done(function(response) {
          navigateTo('assenze', isEdit ? id : response.id, !isEdit);
        });
      });

      $actions.append($save).append($cancel);

      if (isEdit) {
        var $remove = $('<button type="button" class="danger">Rimuovi</button>');
        $remove.on('click', function() {
          var confirmName = data.therapistId ? getSchedulingTherapistName(data.therapistId) : data.name;
          showRemoveModal({
            endpoint: 'Vacations',
            id: id,
            name: confirmName,
            onSuccess: function() {
              navigateTo('assenze');
            }
          });
        });
        $actions.append($remove);
      }

      setTopbarActions($actions);
    }

    // Therapist list is needed synchronously while building the form (for the Assenza
    // dropdown), so it's loaded first - avoids a race where trackDirty's baseline gets
    // captured before the dropdown's options exist.
    loadSchedulingTherapists(function() {
      if (isEdit) {
        $.get('Vacations/Get/' + id).done(function(data) {
          buildForm(data);
        });
      } else {
        buildForm(null);
      }
    });
  }

  // --- Impostazioni (generic Key/Value settings) ----------------------------

  function flashSaveResult($el, success) {
    var cls = success ? 'save-success' : 'save-error';
    $el.addClass(cls);
    setTimeout(function() {
      $el.removeClass(cls);
    }, 800);
  }

  function saveSetting(id, value, $el, onFail) {
    $.ajax({
      url: 'Settings/Update/' + id,
      method: 'PUT',
      contentType: 'application/json',
      data: JSON.stringify({ value: value })
    })
      .done(function() {
        flashSaveResult($el, true);
      })
      .fail(function(jqXHR) {
        flashSaveResult($el, false);
        if (onFail) {
          onFail();
        }
        var message = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Salvataggio non riuscito.';
        window.showModal('<p>' + message + '</p>', [{ label: 'Chiudi', className: 'secondary', onClick: function() {} }]);
      });
  }

  function renderImpostazioniList() {
    $.get('Settings/List', { includeAudit: showAudit })
      .done(function(settings) {
        var $wrapper = $('<div></div>');

        var headerHtml = '<tr><th>Impostazione</th><th>Valore</th>';
        if (showAudit) {
          headerHtml += '<th>Modificatore</th><th>Data Modifica</th>';
        }
        headerHtml += '</tr>';

        var $table = $('<table class="data-table"><thead>' + headerHtml + '</thead><tbody></tbody></table>');
        var $tbody = $table.find('tbody');

        settings.forEach(function(setting) {
          var meta = settingLabels[setting.key];
          var label = meta ? meta.label : setting.key;
          var isTime = meta ? meta.isTime : false;

          var $row = $('<tr></tr>');
          $row.append($('<td></td>').text(label));

          var $valueCell = $('<td></td>');

          if (isTime) {
            // Same time-box + scrollable popup used on the Utenti availability grid,
            // but each selection auto-saves immediately instead of feeding a dirty tracker.
            var $box = $('<button type="button" class="availability-time-box"></button>');
            $box.data('slot', setting.value);
            $box.text(slotToTime(setting.value));

            $box.on('click', function(e) {
              e.stopPropagation();
              openTimePicker($box, $box.data('slot'), function(newSlot) {
                var previousSlot = $box.data('slot');
                var previousText = $box.text();
                $box.data('slot', newSlot);
                $box.text(slotToTime(newSlot));
                saveSetting(setting.id, newSlot, $box, function() {
                  $box.data('slot', previousSlot);
                  $box.text(previousText);
                });
                if (setting.key === 'AvailabilityStart' || setting.key === 'AvailabilityEnd') {
                  availabilityRangeCache = null; // stale after either half of the pair changes - refetched on next use
                }
              });
            });

            $valueCell.append($box);
          } else {
            var $input = $('<input type="number" class="setting-value-input">').val(setting.value);

            $input.on('change', function() {
              var newValue = parseInt($input.val(), 10);

              if (isNaN(newValue)) {
                $input.val(setting.value);
                return;
              }

              var previousValue = setting.value;
              setting.value = newValue;
              saveSetting(setting.id, newValue, $input, function() {
                setting.value = previousValue;
                $input.val(previousValue);
              });
            });

            $valueCell.append($input);
          }

          $row.append($valueCell);

          if (showAudit) {
            $row.append($('<td></td>').text(setting.modifier || ''));
            $row.append($('<td></td>').text(formatDate(setting.modDate)));
          }

          $tbody.append($row);
        });

        $wrapper.append($table);
        $('#content').empty().append($wrapper);
      });
  }

  // Generic modal helper (confirmations, etc.).
  // actions: array of { label, className, onClick }
  // options.preventBackdropClose: when true, clicking the dimmed backdrop does NOT
  // dismiss this modal - used for popups where an accidental outside click
  // shouldn't discard in-progress input (e.g. the Pacchetto confirm dialog).
  var modalPreventBackdropClose = false;

  window.showModal = function(bodyHtml, actions, options) {
    modalPreventBackdropClose = !!(options && options.preventBackdropClose);

    $('#modal-body').html(bodyHtml);
    $('#modal-actions').empty();

    actions.forEach(function(action) {
      var $btn = $('<button>').text(action.label).addClass(action.className || '');
      $btn.on('click', function() {
        $('#modal-overlay').hide();
        if (action.onClick) {
          action.onClick();
        }
      });
      $('#modal-actions').append($btn);
    });

    $('#modal-overlay').show();
  };

  // Clicking the dimmed backdrop (not the modal box itself) dismisses the modal,
  // same as pressing a "Chiudi"/"Annulla" button but without running its onClick -
  // unless the modal opted out via preventBackdropClose above.
  $('#modal-overlay').on('click', function(e) {
    if (e.target === this && !modalPreventBackdropClose) {
      $('#modal-overlay').hide();
    }
  });

  // --- Pacchetti (commercial packages, patient-linked or generic) ------------

  function renderPacchettiList() {
    $.get('Pacchetti/List', { includeAudit: showAudit }).done(function(rows) {
      var $newButton = $('<button type="button">Aggiungi</button>');
      $newButton.on('click', function() {
        navigateTo('pacchetti', 'new');
      });

      var $tariffarioButton = $('<button type="button" class="secondary">Tariffario</button>');
      $tariffarioButton.on('click', function() {
        navigateTo('pacchettiTariffario');
      });

      var $actions = $('<div></div>');
      $actions.append($newButton).append($tariffarioButton);
      setTopbarActions($actions);

      var $wrapper = $('<div></div>');

      var $filter = $('<input type="text" class="filter-input" placeholder="Filtra per nome, paziente o terapia...">');
      $wrapper.append($filter);

      var $table = $(
        '<table class="data-table"><thead><tr>' +
        '<th>Nome</th><th>Approvatore</th><th>Paziente</th><th>Terapie</th><th>Prezzo finale</th><th>Data creazione</th>' +
        (showAudit ? '<th>Modificatore</th><th>Data Modifica</th>' : '') +
        '</tr></thead><tbody></tbody></table>'
      );
      var $tbody = $table.find('tbody');

      rows.forEach(function(p) {
        var $row = $('<tr></tr>');
        $row.data('search', ((p.name || '') + ' ' + (p.patientName || '') + ' ' + (p.itemsSummary || '')).toLowerCase());

        $row.append($('<td></td>').text(p.name));
        $row.append($('<td></td>').text(p.approvatore || ''));
        $row.append($('<td></td>').text(p.patientName));
        $row.append($('<td></td>').text(p.itemsSummary));
        $row.append($('<td></td>').text(formatCurrency(p.totalPrice)));
        $row.append($('<td></td>').text(formatDate(p.createdAt)));

        if (showAudit) {
          $row.append($('<td></td>').text(p.modifier || ''));
          $row.append($('<td></td>').text(formatDate(p.modDate)));
        }

        $row.on('click', function() {
          navigateTo('pacchetti', p.id);
        });

        $tbody.append($row);
      });

      $wrapper.append($table);
      $('#content').empty().append($wrapper);

      $filter.on('input', function() {
        var term = $(this).val().toLowerCase();
        $tbody.find('tr').each(function() {
          var $row = $(this);
          $row.toggle($row.data('search').indexOf(term) !== -1);
        });
      });
    });
  }

  function formatCurrency(value) {
    if (value === null || value === undefined) {
      return '';
    }
    return Number(value).toLocaleString('it-IT', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' €';
  }

  var pacchettoConfigCache = null; // [{ id, therapyTypeId, therapyTypeName, unitPrice, defaultDiscountPercent }]

  function loadPacchettoConfig(callback) {
    if (pacchettoConfigCache) {
      callback();
      return;
    }
    $.get('Pacchetti/Config/List', { includeAudit: showAudit }).done(function(configs) {
      pacchettoConfigCache = configs;
      callback();
    });
  }

  function renderPacchettoTariffarioPage() {
    var $chiudiBtn = $('<button type="button" class="secondary">Chiudi</button>');
    $chiudiBtn.on('click', function() {
      navigateTo('pacchetti');
    });
    setTopbarActions($chiudiBtn);

    pacchettoConfigCache = null;

    loadPacchettoConfig(function() {
      loadTherapyTypes(function() {
        var $wrapper = $('<div></div>');
        $wrapper.append('<h2>Tariffario</h2>');

        var $table = $(
          '<table class="data-table"><thead><tr>' +
          '<th>Terapia</th><th>Prezzo unitario</th><th>Sconto predefinito %</th>' +
          (showAudit ? '<th>Modificatore</th><th>Data Modifica</th>' : '') +
          '</tr></thead><tbody></tbody></table>'
        );
        var $tbody = $table.find('tbody');

        therapyTypesCache.forEach(function(t) {
          var existing = pacchettoConfigCache.filter(function(c) { return c.therapyTypeId === t.id; })[0] || null;
          var configId = existing ? existing.id : null;

          var $row = $('<tr></tr>');
          var $price = $('<input type="number" step="0.01" style="width:100%">').val(existing ? existing.unitPrice : 0);
          var $discount = $('<input type="number" step="0.01" min="0" max="100" style="width:100%">').val(existing ? existing.defaultDiscountPercent : 0);

          function saveRow() {
            var payload = {
              therapyTypeId: t.id,
              unitPrice: parseFloat($price.val()) || 0,
              defaultDiscountPercent: parseFloat($discount.val()) || 0
            };

            var request = configId
              ? $.ajax({ url: 'Pacchetti/Config/Update/' + configId, method: 'PUT', contentType: 'application/json', data: JSON.stringify(payload) })
              : $.ajax({ url: 'Pacchetti/Config/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload) });

            request.done(function(result) {
              if (!configId && result && result.id) {
                configId = result.id;
              }
              pacchettoConfigCache = null;
            });
          }

          $price.on('blur', saveRow);
          $discount.on('blur', saveRow);

          $row.append($('<td></td>').text(t.name));
          $row.append($('<td></td>').append($price));
          $row.append($('<td></td>').append($discount));
          if (showAudit) {
            $row.append($('<td></td>').text(existing && existing.modifier ? existing.modifier : ''));
            $row.append($('<td></td>').text(existing && existing.modDate ? formatDate(existing.modDate) : ''));
          }
          $tbody.append($row);
        });

        $wrapper.append($table);
        $('#content').empty().append($wrapper);
      });
    });
  }

  function renderPacchettoForm(id) {
    var isEdit = id !== null;

    function buildForm(data) {
      pacchettoConfigCache = null; // always fetch fresh - the rate card may have changed since it was last loaded
      loadTherapyTypes(function() {
        loadPacchettoConfig(function() {
          var $form = $('<div class="form-box"></div>');

          var selectedPatientId = data && data.patientId ? data.patientId : null;

          $form.append('<label for="pacchetto-name">Nome</label>');
          var $nameInput = $('<input type="text" id="pacchetto-name" value="' + (data && data.name ? data.name : '') + '">');
          $form.append($nameInput);

          $form.append('<label>Tipo</label>');
          var $scopeList = $('<div class="pacchetto-scope-list"></div>');
          var $isGenericoRadio = $('<label class="pacchetto-scope-option"><input type="radio" class="PacketTypeRadio" name="pacchetto-scope" value="generico"><span>Generico</span></label>');
          var $isPatientRadio = $('<label class="pacchetto-scope-option"><input type="radio" class="PacketTypeRadio" name="pacchetto-scope" value="paziente"><span>Per un paziente specifico</span></label>');
          $scopeList.append($isGenericoRadio).append($isPatientRadio);
          $form.append($scopeList);

          // Search finds a real Patient (sets selectedPatientId, fills the name field
          // with their name); typing directly into the name field is a free-text
          // person - any manual edit to it clears selectedPatientId, since the two
          // are mutually exclusive (CPU's call). Either one filled satisfies
          // "Specifico"; both null means Generico.
          var $patientSearchArea = $('<div class="patient-therapy-form-area"></div>');
          var $patientSearchRow = $('<div class="pacchetto-search-name-row"></div>');
          var $patientSearchInput = $('<input type="text" placeholder="Cerca per nome o telefono (min. 3 caratteri)">');
          var $patientResults = $('<ul class="therapy-parts-list"></ul>');
          var $patientNameInput = $('<input type="text" placeholder="Nome persona" class="pacchetto-person-name">').val(data ? (data.patientName || data.personName || '') : '');
          $patientSearchRow.append($patientSearchInput).append($patientNameInput);
          $patientSearchArea.append($patientSearchRow).append($patientResults);
          $form.append($patientSearchArea);

          function refreshScopeVisibility() {
            var isPatientScope = $isPatientRadio.find('input').is(':checked');
            $patientSearchArea.toggle(isPatientScope);
          }

          $patientSearchInput.on('input', function() {
            var term = $(this).val();
            $patientResults.empty();

            if (term.length < 3) {
              return;
            }

            $.get('Patients/List', { filter: term, page: 1 }).done(function(result) {
              $patientResults.empty();
              result.rows.forEach(function(p) {
                var $li = $('<li style="cursor:pointer;"></li>').text(p.name + ' - ' + formatPhoneDisplay(p.phone));
                $li.on('click', function() {
                  selectedPatientId = p.id;
                  $patientNameInput.val(p.name); // programmatic - does not fire 'input', so the link isn't cleared
                  $patientSearchInput.val('');
                  $patientResults.empty();
                });
                $patientResults.append($li);
              });
            });
          });

          // Only a genuine keystroke fires 'input' - .val() from the search-result
          // click above does not, so picking a patient never immediately clears itself.
          $patientNameInput.on('input', function() {
            selectedPatientId = null;
          });

          if (selectedPatientId || (data && data.personName)) {
            $isPatientRadio.find('input').prop('checked', true);
          } else {
            $isGenericoRadio.find('input').prop('checked', true);
          }
          refreshScopeVisibility();

          $scopeList.find('input').on('change', function() {
            if ($isGenericoRadio.find('input').is(':checked')) {
              selectedPatientId = null;
              $patientNameInput.val('');
            }
            refreshScopeVisibility();
          });

          $form.append('<label>Terapie</label>');

          var $grid = $(
            '<table class="data-table pacchetto-grid"><thead><tr>' +
            '<th>Terapia</th><th>Sedute</th><th>Prezzo unitario</th><th>Prezzo pieno</th><th>Sconto %</th><th>Prezzo finale</th><th></th>' +
            '</tr></thead><tbody></tbody></table>'
          );
          var $gridBody = $grid.find('tbody');
          $form.append($grid);

          function configFor(therapyTypeId) {
            return pacchettoConfigCache.filter(function(c) { return c.therapyTypeId === therapyTypeId; })[0] || null;
          }

          // Unit price is tracked as data on the row itself, set whenever the
          // Terapia dropdown changes (prefilled from the rate card), and displayed
          // read-only in its own column (CPU's call). Prezzo finale is a one-way
          // derived value (Sedute/Sconto % drive it) but stays a plain editable
          // input so it can be overridden - editing it does NOT feed back into
          // Sconto % (CPU's call).
          function recomputeRow($row) {
            var sessions = parseInt($row.find('.pacchetto-item-sessions').val(), 10) || 0;
            var unitPrice = parseFloat($row.data('unitPrice')) || 0;
            var discount = parseFloat($row.find('.pacchetto-item-discount').val()) || 0;

            var full = sessions * unitPrice;
            var final = full * (1 - discount / 100);

            $row.find('.pacchetto-item-unit-price-display').text(formatCurrency(unitPrice));
            $row.find('.pacchetto-item-full-price').text(formatCurrency(full));
            $row.find('.pacchetto-item-final-price-input').val(final.toFixed(2));

            recomputeProposedTotal();
          }

          function addItemRow(therapyTypeId, sessionCount, unitPrice, discountPercent) {
            var $row = $('<tr class="pacchetto-item-row"></tr>');
            var $typeSelect = $('<select class="pacchetto-item-type"></select>');

            therapyTypesCache.forEach(function(t) {
              $typeSelect.append('<option value="' + t.id + '">' + t.name + '</option>');
            });

            if (therapyTypeId) {
              $typeSelect.val(therapyTypeId);
            }

            var initialConfig = configFor(parseInt($typeSelect.val(), 10));
            var initialUnitPrice = unitPrice !== undefined ? unitPrice : (initialConfig ? initialConfig.unitPrice : 0);
            $row.data('unitPrice', initialUnitPrice);

            var $sessionsInput = $('<input type="number" class="pacchetto-item-sessions" min="1" value="' + (sessionCount || 10) + '">');
            var $discountInput = $('<input type="number" step="0.01" min="0" max="100" class="pacchetto-item-discount" value="' +
              (discountPercent !== undefined ? discountPercent : (initialConfig ? initialConfig.defaultDiscountPercent : 0)) + '">');
            var $finalPriceInput = $('<input type="number" step="0.01" class="pacchetto-item-final-price-input">');

            $typeSelect.on('change', function() {
              var config = configFor(parseInt($typeSelect.val(), 10));
              $row.data('unitPrice', config ? config.unitPrice : 0);
              if (config) {
                $discountInput.val(config.defaultDiscountPercent);
              }
              recomputeRow($row);
            });

            $sessionsInput.on('input', function() { recomputeRow($row); });
            $discountInput.on('input', function() { recomputeRow($row); });
            $finalPriceInput.on('input', function() { recomputeProposedTotal(); });

            var $removeBtn = $('<button type="button" class="availability-remove">×</button>');
            $removeBtn.on('click', function() {
              $row.remove();
              recomputeProposedTotal();
            });

            $row.append($('<td></td>').append($typeSelect));
            $row.append($('<td></td>').append($sessionsInput));
            $row.append($('<td class="pacchetto-item-unit-price-display"></td>'));
            $row.append($('<td class="pacchetto-item-full-price"></td>'));
            $row.append($('<td></td>').append($discountInput));
            $row.append($('<td></td>').append($finalPriceInput));
            $row.append($('<td></td>').append($removeBtn));

            $gridBody.append($row);
            recomputeRow($row);
          }

          if (data && data.items && data.items.length > 0) {
            data.items.forEach(function(i) { addItemRow(i.therapyTypeId, i.sessionCount, i.unitPrice, i.discountPercent); });
          } else {
            addItemRow(null, 10);
          }

          var $addItemBtn = $('<button type="button" class="availability-add-btn">+ Aggiungi terapia</button>');
          $addItemBtn.on('click', function() { addItemRow(null, 10); });
          $form.append($addItemBtn);

          var $summary = $('<div class="pacchetto-summary"></div>');
          var $normalTotalLabel = $('<div>Costo normale: <strong class="pacchetto-normal-total"></strong></div>');
          var $discountedTotalLabel = $('<div>Costo scontato: <strong class="pacchetto-discounted-total"></strong></div>');
          var $scontoCalcolatoLabel = $('<div>Sconto calcolato: <strong class="pacchetto-sconto-calcolato"></strong></div>');
          var $packetCostLabel = $('<div class="pacchetto-final-cost">Costo scontato a pacchetto: <strong class="pacchetto-packet-cost"></strong></div>');
          $summary.append($normalTotalLabel).append($discountedTotalLabel).append($scontoCalcolatoLabel).append($packetCostLabel);
          $form.append($summary);

          var lastPriceResult = null;

          function recomputeProposedTotal() {
            var items = collectItems();
            if (items.length === 0) {
              lastPriceResult = null;
              $normalTotalLabel.find('strong').text(formatCurrency(0));
              $discountedTotalLabel.find('strong').text(formatCurrency(0));
              $scontoCalcolatoLabel.find('strong').text('0%');
              $packetCostLabel.find('strong').text(formatCurrency(0));
              return;
            }

            $.ajax({
              url: 'Pacchetti/ProposeFinalPrice',
              method: 'POST',
              contentType: 'application/json',
              data: JSON.stringify(items)
            }).done(function(result) {
              lastPriceResult = result;
              $normalTotalLabel.find('strong').text(formatCurrency(result.totalNormal));
              $discountedTotalLabel.find('strong').text(formatCurrency(result.totalDiscounted));
              $scontoCalcolatoLabel.find('strong').text(result.scontoCalcolato + '%');
              $packetCostLabel.find('strong').text(formatCurrency(result.packetCost));
            });
          }

          $form.append('<div id="pacchetto-error" class="form-error"></div>');

          $('#content').empty().append($form);

          function collectItems() {
            var items = [];
            $gridBody.find('.pacchetto-item-row').each(function() {
              var $row = $(this);
              items.push({
                therapyTypeId: parseInt($row.find('.pacchetto-item-type').val(), 10),
                sessionCount: parseInt($row.find('.pacchetto-item-sessions').val(), 10),
                unitPrice: parseFloat($row.data('unitPrice')) || 0,
                discountPercent: parseFloat($row.find('.pacchetto-item-discount').val()) || 0
              });
            });
            return items;
          }

          recomputeProposedTotal();

          var $save = $('<button type="button">' + (isEdit ? 'Salva' : 'Crea') + '</button>');
          var $cancel = $('<button type="button" class="secondary">Cancella</button>');
          $cancel.on('click', function() { navigateTo('pacchetti'); });
          trackDirty($form, $save, $cancel);

          // "Crea"/"Salva" opens the confirm popup (Approvato da / Paziente / Prezzo /
          // Data) instead of saving directly - the actual save happens from inside
          // that popup, using the cost breakdown already computed here.
          $save.on('click', function() {
            var items = collectItems();
            var valid = items.length > 0 && items.every(function(i) {
              return i.therapyTypeId && i.sessionCount >= 1 && i.discountPercent >= 0 && i.discountPercent <= 100;
            });

            if (!valid) {
              $('#pacchetto-error').text('Serve almeno una terapia valida, con almeno una seduta e uno sconto tra 0 e 100.').show();
              return;
            }

            var isPatientScope = $isPatientRadio.find('input').is(':checked');

            if (isPatientScope && !selectedPatientId && !$patientNameInput.val()) {
              $('#pacchetto-error').text('Seleziona un paziente o inserisci un nome, oppure scegli Generico.').show();
              return;
            }

            $('#pacchetto-error').hide();
            showPacchettoConfirmPopup(items, isPatientScope);
          });

          function showPacchettoConfirmPopup(items, isPatientScope) {
            var priceResult = lastPriceResult || { totalNormal: 0, totalDiscounted: 0, packetCost: 0 };
            var personName = isPatientScope ? $patientNameInput.val() : '';

            var $body = $('<div></div>');

            var $costBox = $('<div class="pacchetto-confirm-costs"></div>');
            $costBox.append($('<div></div>').text('Costo normale: ' + formatCurrency(priceResult.totalNormal)));
            $costBox.append($('<div></div>').text('Costo scontato: ' + formatCurrency(priceResult.totalDiscounted)));
            $costBox.append($('<div class="pacchetto-confirm-final-cost"></div>').text('Costo scontato a pacchetto: ' + formatCurrency(priceResult.packetCost)));
            $body.append($costBox);

            $body.append('<label for="pacchetto-confirm-approvatore">Approvato da</label>');
            var $approvatoreInput = $('<input type="text" id="pacchetto-confirm-approvatore" style="margin-bottom: 2em;">')
              .val((data && data.approvatore) || (currentSession ? currentSession.name : ''));
            $body.append($approvatoreInput);

            $body.append('<label>Paziente</label>');
            $body.append($('<input type="text" readonly style="margin-bottom: 2em;">').val(isPatientScope ? personName : 'Generico'));

            $body.append('<label for="pacchetto-confirm-prezzo">Prezzo</label>');
            var $prezzoInput = $('<input type="number" step="0.01" id="pacchetto-confirm-prezzo" style="margin-bottom: 2em;">').val(priceResult.packetCost);
            $body.append($prezzoInput);

            $body.append('<label for="pacchetto-confirm-data">Data</label>');
            var defaultDate = data && data.createdAt ? data.createdAt.split('T')[0] : formatDateISO(new Date());
            var $dataInput = $('<input type="date" id="pacchetto-confirm-data">').val(defaultDate);
            $body.append($dataInput);

            window.showModal('', [
              {
                label: (isEdit ? 'Salva' : 'Crea') + ' pacchetto',
                className: '',
                onClick: function() {
                  var payload = {
                    name: $nameInput.val() || null,
                    patientId: isPatientScope ? selectedPatientId : null,
                    personName: isPatientScope && !selectedPatientId ? (personName || null) : null,
                    approvatore: $approvatoreInput.val(),
                    totalPrice: parseFloat($prezzoInput.val()) || 0,
                    createdAt: $dataInput.val() ? $dataInput.val() + 'T00:00:00' : null,
                    items: items
                  };

                  var request = isEdit
                    ? $.ajax({ url: 'Pacchetti/Update/' + id, method: 'PUT', contentType: 'application/json', data: JSON.stringify(payload) })
                    : $.ajax({ url: 'Pacchetti/Create', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload) });

                  request.done(function() {
                    navigateTo('pacchetti');
                  });

                  request.fail(function(jqXHR) {
                    var message = (jqXHR.responseJSON && jqXHR.responseJSON.message) ? jqXHR.responseJSON.message : 'Salvataggio non riuscito.';
                    $('#pacchetto-error').text(message).show();
                  });
                }
              },
              { label: 'Annulla', className: 'secondary', onClick: function() {} }
            ], { preventBackdropClose: true });
            $('#modal-body').empty().append($body);
          }

          var $actions = $('<div></div>');
          $actions.append($save).append($cancel);

          if (isEdit) {
            var $removeBtn = $('<button type="button" class="danger">Elimina</button>');
            $removeBtn.on('click', function() {
              showRemoveModal({
                endpoint: 'Pacchetti',
                id: id,
                name: data.confirmName,
                onSuccess: function() { navigateTo('pacchetti'); }
              });
            });
            $actions.append($removeBtn);
          }

          setTopbarActions($actions);
        });
      });
    }

    if (isEdit) {
      $.get('Pacchetti/Get/' + id).done(function(data) {
        buildForm(data);
      });
    } else {
      buildForm(null);
    }
  }

  checkSession();
});
