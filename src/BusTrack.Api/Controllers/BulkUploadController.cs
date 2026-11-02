using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using BusTrack.Api.Auth;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BusRoute = BusTrack.Api.Data.Route;

namespace BusTrack.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/bulk-upload")]
public class BulkUploadController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TrackingService _tracking;
    private readonly RouteGeometryService _geometry;
    private readonly PasswordHasher<User> _passwords;

    private static readonly ConcurrentDictionary<string, BulkImportSession> _sessions = new();

    public BulkUploadController(
        AppDbContext db,
        TrackingService tracking,
        RouteGeometryService geometry,
        PasswordHasher<User> passwords)
    {
        _db = db;
        _tracking = tracking;
        _geometry = geometry;
        _passwords = passwords;
    }

    // 1. GET /api/admin/bulk-upload/templates/{type}
    [HttpGet("templates/{type}")]
    public IActionResult DownloadTemplate(string type)
    {
        var normalized = (type ?? "all").Trim().ToLowerInvariant();
        var (fileName, content) = GetTemplateContent(normalized);
        var bytes = Encoding.UTF8.GetBytes(content);
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    // 2. POST /api/admin/bulk-upload/validate
    [HttpPost("validate")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ValidateCsv(
        [FromForm] CsvUploadForm form,
        [FromQuery] string? type,
        CancellationToken ct)
    {
        var file = form.File ?? (Request.HasFormContentType && Request.Form.Files.Count > 0 ? Request.Form.Files["file"] ?? Request.Form.Files[0] : null);
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Please select a valid CSV file to upload." });

        if (file.Length > 15 * 1024 * 1024)
            return BadRequest(new { message = $"File size ({(file.Length / (1024.0 * 1024.0)):F1} MB) exceeds the 15 MB limit." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".csv" && ext != ".txt")
            return BadRequest(new { message = "Only CSV files (.csv) are supported. If using Excel, please save as CSV UTF-8 before uploading." });

        string csvText;
        using (var stream = file.OpenReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            csvText = await reader.ReadToEndAsync(ct);
        }

        var lines = ParseCsvLines(csvText);
        if (lines.Count < 2)
            return BadRequest(new { message = "The CSV file must contain a header row and at least one data row." });

        var headerRow = lines[0].Select(h => h.Trim().Trim('\uFEFF')).ToList();
        var dataRows = lines.Skip(1).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();

        if (dataRows.Count == 0)
            return BadRequest(new { message = "The CSV file contains no data rows." });

        var selectedType = !string.IsNullOrWhiteSpace(type) ? type : form.Type;
        var defaultKind = (selectedType ?? "all").Trim().ToLowerInvariant();
        if (headerRow.Any(h => string.Equals(h, "type", StringComparison.OrdinalIgnoreCase)))
            defaultKind = "all";

        var session = await PerformValidationAsync(file.FileName, file.Length, defaultKind, headerRow, dataRows, ct);
        _sessions[session.ImportId] = session;

        return Ok(session);
    }

    // 3. GET /api/admin/bulk-upload/{importId}
    [HttpGet("{importId}")]
    public IActionResult GetSession(string importId)
    {
        if (_sessions.TryGetValue(importId, out var session))
            return Ok(session);

        return NotFound(new { message = "Import session not found or expired." });
    }

    // 4. POST /api/admin/bulk-upload/{importId}/commit
    [HttpPost("{importId}/commit")]
    public async Task<IActionResult> CommitImport(string importId, [FromQuery] bool updateExisting = false, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(importId, out var session))
            return NotFound(new { message = "Import session not found or expired. Please upload and validate again." });

        if (session.Status == "Completed")
            return BadRequest(new { message = "This import has already been completed." });

        session.Status = "Committing";

        try
        {
            var result = await ExecuteCommitAsync(session, updateExisting, ct);
            session.Status = "Completed";
            session.CommitResult = result;

            await _tracking.Audit(
                GetCurrentUserId(),
                "BulkImport",
                session.Type,
                session.ImportId,
                $"Imported: {result.Imported}, Updated: {result.Updated}, Reused: {result.Reused}, Skipped: {result.Skipped}, Failed: {result.Failed}, Routes: {result.Routes}, Schedules: {result.Schedules}, File: {session.FileName}",
                ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            session.Status = "Failed";
            return StatusCode(500, new { message = $"Import failed during execution: {ex.Message}" });
        }
    }

    public record BulkDeleteByCodesRequest(string Type, List<string> Codes);

    [HttpPost("delete-records")]
    public async Task<IActionResult> DeleteRecordsByCodes([FromBody] BulkDeleteByCodesRequest request, CancellationToken ct)
    {
        if (request?.Codes == null || request.Codes.Count == 0)
            return BadRequest(new { message = "No codes provided to delete." });

        var normalizedType = (request.Type ?? "").Trim().ToLowerInvariant();
        var codes = request.Codes
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();

        if (codes.Count == 0)
            return BadRequest(new { message = "No valid codes provided to delete." });

        int deleted = 0;
        int skipped = 0;
        var reasons = new List<string>();

        try
        {
            if (normalizedType is "stops" or "stop")
            {
                var stops = await _db.Stops.Where(s => codes.Contains(s.Code) && s.IsActive).ToListAsync(ct);
                var stopIds = stops.Select(s => s.Id).ToList();
                var inRoutesSet = stopIds.Count > 0
                    ? (await _db.RouteStops
                        .Where(rs => stopIds.Contains(rs.StopId))
                        .Select(rs => rs.StopId)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet()
                    : new HashSet<int>();

                var matchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var s in stops)
                {
                    matchedCodes.Add(s.Code);
                    if (inRoutesSet.Contains(s.Id))
                    {
                        skipped++;
                        reasons.Add($"Stop '{s.Code}' ({s.Name}) is part of an active route.");
                        continue;
                    }
                    s.IsActive = false;
                    deleted++;
                }

                foreach (var code in codes)
                {
                    if (!matchedCodes.Contains(code))
                    {
                        skipped++;
                        reasons.Add($"Stop '{code}' was not found or is already inactive.");
                    }
                }
            }
            else if (normalizedType is "buses" or "bus")
            {
                var buses = await _db.Buses.Where(b => (codes.Contains(b.FleetNumber) || codes.Contains(b.RegistrationNumber)) && b.IsActive).ToListAsync(ct);
                var busIds = buses.Select(b => b.Id).ToList();
                var inSchedSet = busIds.Count > 0
                    ? (await _db.Schedules
                        .Where(s => s.BusId.HasValue && busIds.Contains(s.BusId.Value) && s.IsActive)
                        .Select(s => s.BusId!.Value)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet()
                    : new HashSet<int>();

                var matchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var b in buses)
                {
                    matchedCodes.Add(b.FleetNumber);
                    matchedCodes.Add(b.RegistrationNumber);
                    if (inSchedSet.Contains(b.Id))
                    {
                        skipped++;
                        reasons.Add($"Bus '{b.FleetNumber}' ({b.RegistrationNumber}) is currently assigned to an active schedule.");
                        continue;
                    }
                    b.IsActive = false;
                    deleted++;
                }

                foreach (var code in codes)
                {
                    if (!matchedCodes.Contains(code))
                    {
                        skipped++;
                        reasons.Add($"Bus '{code}' was not found or is already inactive.");
                    }
                }
            }
            else if (normalizedType is "drivers" or "driver")
            {
                var drivers = await _db.Drivers
                    .Include(d => d.User)
                    .Where(d => d.IsActive && ((d.User.DriverCode != null && codes.Contains(d.User.DriverCode)) || codes.Contains(d.LicenseNumber)))
                    .ToListAsync(ct);
                var drvIds = drivers.Select(d => d.Id).ToList();
                var inSchedSet = drvIds.Count > 0
                    ? (await _db.Schedules
                        .Where(s => s.DriverId.HasValue && drvIds.Contains(s.DriverId.Value) && s.IsActive)
                        .Select(s => s.DriverId!.Value)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet()
                    : new HashSet<int>();

                var matchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var d in drivers)
                {
                    if (!string.IsNullOrEmpty(d.User?.DriverCode)) matchedCodes.Add(d.User.DriverCode);
                    if (!string.IsNullOrEmpty(d.LicenseNumber)) matchedCodes.Add(d.LicenseNumber);

                    if (inSchedSet.Contains(d.Id))
                    {
                        skipped++;
                        var dCode = d.User?.DriverCode ?? d.LicenseNumber;
                        reasons.Add($"Driver '{dCode}' is assigned to an active schedule.");
                        continue;
                    }
                    d.IsActive = false;
                    if (d.User != null) d.User.IsActive = false;
                    deleted++;
                }

                foreach (var code in codes)
                {
                    if (!matchedCodes.Contains(code))
                    {
                        skipped++;
                        reasons.Add($"Driver '{code}' was not found or is already inactive.");
                    }
                }
            }
            else if (normalizedType is "operators" or "operator")
            {
                var ops = await _db.Operators.Where(o => codes.Contains(o.Code) && o.IsActive).ToListAsync(ct);
                var opIds = ops.Select(o => o.Id).ToList();
                var hasBusesSet = opIds.Count > 0
                    ? (await _db.Buses
                        .Where(b => opIds.Contains(b.OperatorId) && b.IsActive)
                        .Select(b => b.OperatorId)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet()
                    : new HashSet<int>();

                var matchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var o in ops)
                {
                    matchedCodes.Add(o.Code);
                    if (hasBusesSet.Contains(o.Id))
                    {
                        skipped++;
                        reasons.Add($"Operator '{o.Code}' has active buses assigned.");
                        continue;
                    }
                    o.IsActive = false;
                    deleted++;
                }

                foreach (var code in codes)
                {
                    if (!matchedCodes.Contains(code))
                    {
                        skipped++;
                        reasons.Add($"Operator '{code}' was not found or is already inactive.");
                    }
                }
            }
            else if (normalizedType is "routes" or "route")
            {
                var routes = await _db.Routes.Where(r => codes.Contains(r.Code) && r.IsActive).ToListAsync(ct);
                var routeIds = routes.Select(r => r.Id).ToList();
                var hasSchedSet = routeIds.Count > 0
                    ? (await _db.Schedules
                        .Where(s => routeIds.Contains(s.RouteId) && s.IsActive)
                        .Select(s => s.RouteId)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet()
                    : new HashSet<int>();

                var matchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var r in routes)
                {
                    matchedCodes.Add(r.Code);
                    if (hasSchedSet.Contains(r.Id))
                    {
                        skipped++;
                        reasons.Add($"Route '{r.Code}' ({r.Name}) still has active schedules.");
                        continue;
                    }
                    r.IsActive = false;
                    deleted++;
                }

                foreach (var code in codes)
                {
                    if (!matchedCodes.Contains(code))
                    {
                        skipped++;
                        reasons.Add($"Route '{code}' was not found or is already inactive.");
                    }
                }
            }
            else if (normalizedType is "schedules" or "schedule")
            {
                var scheduleIdList = new List<int>();
                var routeTimePairs = new List<(string RouteCode, string Time)>();

                foreach (var c in codes)
                {
                    var clean = c.Trim();
                    if (clean.StartsWith("SCH-", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(clean[4..], out var sid)) scheduleIdList.Add(sid);
                    }
                    else if (int.TryParse(clean, out var sid))
                    {
                        scheduleIdList.Add(sid);
                    }
                    else if (clean.Contains('@'))
                    {
                        var parts = clean.Split('@');
                        if (parts.Length == 2) routeTimePairs.Add((parts[0].Trim(), parts[1].Trim()));
                    }
                }

                var schedules = await _db.Schedules
                    .Include(s => s.Route)
                    .Where(s => s.IsActive && (scheduleIdList.Contains(s.Id) || (s.Route != null && codes.Contains(s.Route.Code))))
                    .ToListAsync(ct);

                if (routeTimePairs.Count > 0)
                {
                    var extra = await _db.Schedules
                        .Include(s => s.Route)
                        .Where(s => s.IsActive && s.Route != null)
                        .ToListAsync(ct);
                    foreach (var s in extra)
                    {
                        var timeStr = s.DepartureTime.ToString("HH:mm");
                        if (routeTimePairs.Any(p => string.Equals(p.RouteCode, s.Route.Code, StringComparison.OrdinalIgnoreCase) && (p.Time == timeStr || p.Time == s.DepartureTime.ToString("H:mm"))))
                        {
                            if (!schedules.Any(existing => existing.Id == s.Id))
                                schedules.Add(s);
                        }
                    }
                }

                var scheduleIds = schedules.Select(s => s.Id).ToList();
                var runningScheduleIds = scheduleIds.Count > 0
                    ? (await _db.Trips
                        .Where(t => scheduleIds.Contains(t.ScheduleId) && t.Status == "Running")
                        .Select(t => t.ScheduleId)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet()
                    : new HashSet<int>();

                var matchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var s in schedules)
                {
                    matchedCodes.Add(s.Id.ToString());
                    matchedCodes.Add($"SCH-{s.Id}");
                    if (s.Route != null)
                    {
                        matchedCodes.Add($"{s.Route.Code}@{s.DepartureTime:HH:mm}");
                        matchedCodes.Add(s.Route.Code);
                    }

                    if (runningScheduleIds.Contains(s.Id))
                    {
                        skipped++;
                        var rCode = s.Route?.Code ?? s.RouteId.ToString();
                        reasons.Add($"Schedule for '{rCode}' ({s.DepartureTime:HH:mm}) currently has a live trip running.");
                        continue;
                    }

                    s.IsActive = false;
                    deleted++;
                }

                foreach (var code in codes)
                {
                    if (!matchedCodes.Contains(code))
                    {
                        skipped++;
                        reasons.Add($"Schedule '{code}' was not found or is already inactive.");
                    }
                }
            }
            else
            {
                return BadRequest(new { message = $"Unsupported entity type '{request.Type}'." });
            }

            if (deleted > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            var auditEntityId = codes.Count == 1 ? (codes[0].Length > 40 ? codes[0][..40] : codes[0]) : $"{codes.Count} {normalizedType}"[..Math.Min(40, $"{codes.Count} {normalizedType}".Length)];
            var auditDetail = $"Codes: {string.Join(",", codes)}. Deleted: {deleted}, skipped: {skipped}.";
            if (auditDetail.Length > 500) auditDetail = auditDetail[..497] + "...";

            await _tracking.Audit(GetCurrentUserId(), "BulkDeleteByCodes", request.Type ?? "", auditEntityId, auditDetail, ct);

            return Ok(new { deleted, skipped, reasons, message = $"Deleted {deleted} record(s). {skipped} skipped." });
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException != null ? $"{ex.Message} -> {ex.InnerException.Message}" : ex.Message;
            return StatusCode(500, new { message = $"Failed to execute bulk delete: {msg}" });
        }
    }

    // 5. GET /api/admin/bulk-upload/{importId}/errors
    [HttpGet("{importId}/errors")]
    public IActionResult DownloadErrorReport(string importId)
    {
        if (!_sessions.TryGetValue(importId, out var session))
            return NotFound(new { message = "Import session not found." });

        var errorRows = session.Rows.Where(r => r.Errors.Count > 0 || r.Status == "Failed" || r.Status == "Skipped").ToList();
        if (errorRows.Count == 0)
            return BadRequest(new { message = "No errors were found in this import session." });

        var sb = new StringBuilder();
        sb.AppendLine("CSV Row,Record Type,Identifier,Status,Errors,Warnings");

        foreach (var r in errorRows)
        {
            var errorsJoined = string.Join("; ", r.Errors).Replace("\"", "\"\"");
            var warningsJoined = string.Join("; ", r.Warnings).Replace("\"", "\"\"");
            sb.AppendLine($"{r.Row},\"{r.Type}\",\"{r.Code}\",\"{r.Status}\",\"{errorsJoined}\",\"{warningsJoined}\"");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv; charset=utf-8", $"import-errors-{importId}.csv");
    }

    // 6. GET /api/admin/bulk-upload/history
    [HttpGet("history")]
    public async Task<IActionResult> GetImportHistory(CancellationToken ct)
    {
        var logs = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "BulkImport")
            .OrderByDescending(a => a.CreatedAt)
            .Take(25)
            .Select(a => new
            {
                importId = a.EntityId ?? a.Id.ToString(),
                date = a.CreatedAt,
                admin = a.UserId.HasValue ? a.UserId.Value.ToString() : "Admin",
                details = a.Detail ?? "Bulk Import",
                status = "Completed"
            })
            .ToListAsync(ct);

        var activeSessions = _sessions.Values
            .OrderByDescending(s => s.CreatedAt)
            .Take(10)
            .Select(s => new
            {
                importId = s.ImportId,
                date = s.CreatedAt,
                admin = s.AdminName,
                details = $"{s.FileName} ({s.TotalRows} rows: {s.ReadyCount} ready, {s.ReusedCount} reused, {s.InvalidCount} invalid)",
                status = s.Status
            })
            .ToList();

        return Ok(new { activeSessions, auditHistory = logs });
    }

    #region Helpers & Validation

    private Guid? GetCurrentUserId()
    {
        try
        {
            var val = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(val, out var parsed)) return parsed;
        }
        catch { }
        return null;
    }

    private static string GetOperatorCode(Dictionary<string, string> dict)
    {
        var opId = dict.GetValueOrDefault("operatorId")?.Trim() ?? "";
        if (!string.IsNullOrEmpty(opId) && !opId.All(char.IsDigit))
            return opId.ToUpperInvariant();

        var opCode = dict.GetValueOrDefault("operatorCode")?.Trim() ?? "";
        if (!string.IsNullOrEmpty(opCode))
            return opCode.ToUpperInvariant();

        return (dict.GetValueOrDefault("code")?.Trim() ?? "").ToUpperInvariant();
    }

    private static string GetDriverCode(Dictionary<string, string> dict)
    {
        return (dict.GetValueOrDefault("driverCode")?.Trim() ?? dict.GetValueOrDefault("code")?.Trim() ?? "").ToUpperInvariant();
    }

    private static string NormalizeDriverCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        var upper = code.Trim().ToUpperInvariant();
        return upper.Replace("-ID-", "-");
    }

    private async Task<BulkImportSession> PerformValidationAsync(
        string fileName,
        long fileSize,
        string kind,
        List<string> headers,
        List<List<string>> rows,
        CancellationToken ct)
    {
        var session = new BulkImportSession
        {
            FileName = fileName,
            FileSize = fileSize,
            Type = kind,
            TotalRows = rows.Count,
            AdminName = User.Identity?.Name ?? "Admin"
        };

        var dbOperators = await _db.Operators.AsNoTracking().ToListAsync(ct);
        var dbDrivers = await _db.Drivers.AsNoTracking().Include(d => d.User).ToListAsync(ct);
        var dbBuses = await _db.Buses.AsNoTracking().ToListAsync(ct);
        var dbStops = await _db.Stops.AsNoTracking().ToListAsync(ct);

        var dbOpCodes = dbOperators.Select(o => o.Code.Trim().ToUpperInvariant()).ToHashSet();
        var dbDrvCodes = dbDrivers.Select(d => d.User.DriverCode?.Trim().ToUpperInvariant() ?? "").Where(c => c.Length > 0).ToHashSet();
        var dbDrvEmails = dbDrivers.Select(d => d.User.Email.Trim().ToLowerInvariant()).ToHashSet();
        var dbFleetNums = dbBuses.Select(b => b.FleetNumber.Trim().ToUpperInvariant()).ToHashSet();
        var dbBusRegs = dbBuses.Select(b => b.RegistrationNumber.Trim().ToUpperInvariant()).ToHashSet();
        var dbStopCodes = dbStops.Select(s => s.Code.Trim().ToUpperInvariant()).ToHashSet();
        var dbStopNames = new Dictionary<string, Stop>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in dbStops)
        {
            var norm = StopNormalizer.NormalizeName(s.Name);
            if (!string.IsNullOrEmpty(norm) && !dbStopNames.ContainsKey(norm))
                dbStopNames[norm] = s;
        }

        var fileOpCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileDrvCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileDrvEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: Index all pending codes from file to resolve forward references
        for (int i = 0; i < rows.Count; i++)
        {
            var dict = RowToDictionary(headers, rows[i]);
            var rowType = DetectRowType(dict, kind);
            if (rowType == "operators" || rowType == "operator")
            {
                var code = GetOperatorCode(dict);
                if (!string.IsNullOrEmpty(code)) fileOpCodes.Add(code);
            }
            else if (rowType == "drivers" || rowType == "driver")
            {
                var code = GetDriverCode(dict);
                if (!string.IsNullOrEmpty(code)) fileDrvCodes.Add(code);
            }
        }

        var emailRegex = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
        var phoneRegex = new Regex(@"^[\d\+\-\s\(\)]{7,20}$", RegexOptions.Compiled);

        var seenOpInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDrvInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmailInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFleetInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRegInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStopInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStopNamesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 2: Comprehensive Row-by-Row Validation
        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var dict = RowToDictionary(headers, rows[i]);
            var rowType = DetectRowType(dict, kind);

            var item = new BulkRowResult
            {
                Row = rowNum,
                Type = rowType,
                Data = dict
            };

            switch (rowType.ToLowerInvariant())
            {
                case "operators":
                case "operator":
                    ValidateOperatorRow(dict, item, dbOpCodes, seenOpInFile, emailRegex, phoneRegex);
                    break;
                case "drivers":
                case "driver":
                    ValidateDriverRow(dict, item, dbDrvCodes, dbDrvEmails, seenDrvInFile, seenEmailInFile, dbOpCodes, fileOpCodes, emailRegex, phoneRegex);
                    break;
                case "stops":
                case "stop":
                    ValidateStopRow(dict, item, dbStops, dbStopCodes, dbStopNames, seenStopInFile, seenStopNamesInFile);
                    break;
                case "buses":
                case "bus":
                    ValidateBusRow(dict, item, dbFleetNums, dbBusRegs, seenFleetInFile, seenRegInFile, dbOpCodes, fileOpCodes, dbDrvCodes, fileDrvCodes);
                    break;
                default:
                    item.Errors.Add($"Unknown record type: '{rowType}'. Expected operator, driver, stop, or bus.");
                    item.Status = "Error";
                    break;
            }

            session.Rows.Add(item);
        }

        session.ReadyCount = session.Rows.Count(r => r.Status == "Ready");
        session.ReusedCount = session.Rows.Count(r => r.Status == "Reused");
        session.InvalidCount = session.Rows.Count(r => r.Status == "Error");
        session.WarningCount = session.Rows.Count(r => r.Status == "Warning");

        return session;
    }

    private void ValidateOperatorRow(
        Dictionary<string, string> dict,
        BulkRowResult item,
        HashSet<string> dbOpCodes,
        HashSet<string> seenInFile,
        Regex emailRegex,
        Regex phoneRegex)
    {
        var name = dict.GetValueOrDefault("name")?.Trim() ?? "";
        var code = GetOperatorCode(dict);
        var email = dict.GetValueOrDefault("email")?.Trim() ?? "";
        var phone = dict.GetValueOrDefault("phone")?.Trim() ?? "";

        item.Name = name;
        item.Code = code;

        if (string.IsNullOrEmpty(name)) item.Errors.Add("Operator Name is required.");
        if (string.IsNullOrEmpty(code)) item.Errors.Add("Operator Code is required.");

        if (!string.IsNullOrEmpty(email) && !emailRegex.IsMatch(email))
            item.Errors.Add($"Invalid email format: '{email}'.");

        if (!string.IsNullOrEmpty(phone) && !phoneRegex.IsMatch(phone))
            item.Warnings.Add($"Phone number '{phone}' may be formatted unusually.");

        if (!string.IsNullOrEmpty(code))
        {
            if (seenInFile.Contains(code))
                item.Errors.Add($"Duplicate operator code '{code}' repeated in this CSV.");
            else
                seenInFile.Add(code);

            if (dbOpCodes.Contains(code))
            {
                item.Status = "Reused";
                item.Warnings.Add($"Operator '{code}' already exists in the database and will be safely reused.");
                return;
            }
        }

        item.Status = item.Errors.Count > 0 ? "Error" : item.Warnings.Count > 0 ? "Warning" : "Ready";
    }

    private void ValidateDriverRow(
        Dictionary<string, string> dict,
        BulkRowResult item,
        HashSet<string> dbDrvCodes,
        HashSet<string> dbDrvEmails,
        HashSet<string> seenCodesInFile,
        HashSet<string> seenEmailsInFile,
        HashSet<string> dbOpCodes,
        HashSet<string> fileOpCodes,
        Regex emailRegex,
        Regex phoneRegex)
    {
        var fullName = (dict.GetValueOrDefault("fullName") ?? dict.GetValueOrDefault("name") ?? "").Trim();
        var driverCode = GetDriverCode(dict);
        var email = dict.GetValueOrDefault("email")?.Trim().ToLowerInvariant() ?? "";
        var phone = dict.GetValueOrDefault("phone")?.Trim() ?? "";
        var license = dict.GetValueOrDefault("licenseNumber")?.Trim() ?? "";
        var opCode = (dict.GetValueOrDefault("operatorCode") ?? dict.GetValueOrDefault("operatorId") ?? "").Trim().ToUpperInvariant();

        item.Name = fullName;
        item.Code = driverCode;

        if (string.IsNullOrEmpty(fullName)) item.Errors.Add("Driver Full Name is required.");
        if (string.IsNullOrEmpty(driverCode)) item.Errors.Add("Driver Code is required.");
        if (string.IsNullOrEmpty(email)) item.Errors.Add("Driver Email is required.");
        else if (!emailRegex.IsMatch(email)) item.Errors.Add($"Invalid email address format: '{email}'.");

        if (string.IsNullOrEmpty(license)) item.Errors.Add("Driver License Number is required.");

        if (!string.IsNullOrEmpty(phone) && !phoneRegex.IsMatch(phone))
            item.Warnings.Add($"Phone number '{phone}' may be formatted unusually.");

        if (!string.IsNullOrEmpty(opCode) && !dbOpCodes.Contains(opCode) && !fileOpCodes.Contains(opCode))
            item.Warnings.Add($"Referenced operator '{opCode}' was not found in database or this file.");

        if (!string.IsNullOrEmpty(driverCode))
        {
            if (seenCodesInFile.Contains(driverCode))
                item.Errors.Add($"Duplicate driver code '{driverCode}' repeated in this CSV.");
            else
                seenCodesInFile.Add(driverCode);

            if (dbDrvCodes.Contains(driverCode))
            {
                item.Status = "Reused";
                item.Warnings.Add($"Driver '{driverCode}' is already registered and will be safely reused.");
                return;
            }
        }

        if (!string.IsNullOrEmpty(email))
        {
            if (seenEmailsInFile.Contains(email))
                item.Errors.Add($"Driver email '{email}' is used multiple times in this CSV.");
            else
                seenEmailsInFile.Add(email);

            if (dbDrvEmails.Contains(email))
                item.Errors.Add($"Driver email '{email}' is already in use by an existing driver.");
        }

        item.Status = item.Errors.Count > 0 ? "Error" : item.Warnings.Count > 0 ? "Warning" : "Ready";
    }

    private void ValidateStopRow(
        Dictionary<string, string> dict,
        BulkRowResult item,
        List<Stop> dbStops,
        HashSet<string> dbStopCodes,
        Dictionary<string, Stop> dbStopNames,
        HashSet<string> seenCodesInFile,
        HashSet<string> seenNamesInFile)
    {
        var name = dict.GetValueOrDefault("name")?.Trim() ?? "";
        var code = (dict.GetValueOrDefault("code") ?? "").Trim().ToUpperInvariant();
        var latStr = dict.GetValueOrDefault("latitude")?.Trim() ?? "";
        var lonStr = dict.GetValueOrDefault("longitude")?.Trim() ?? "";
        var city = dict.GetValueOrDefault("city")?.Trim();

        item.Name = name;
        item.Code = code;

        if (string.IsNullOrEmpty(name)) item.Errors.Add("Stop Name is required.");
        if (string.IsNullOrEmpty(code)) item.Errors.Add("Stop Code is required.");

        var hasLat = decimal.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat);
        var hasLon = decimal.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon);

        if (!string.IsNullOrEmpty(latStr) || !string.IsNullOrEmpty(lonStr))
        {
            if (!hasLat || lat < -90m || lat > 90m)
                item.Errors.Add("Latitude must be a valid number between -90 and 90.");
            if (!hasLon || lon < -180m || lon > 180m)
                item.Errors.Add("Longitude must be a valid number between -180 and 180.");
        }
        else
        {
            item.Warnings.Add("No coordinates provided. Default coordinates will be used.");
        }

        var normName = StopNormalizer.NormalizeName(name);

        if (!string.IsNullOrEmpty(normName))
        {
            if (seenNamesInFile.Contains(normName))
                item.Errors.Add($"Duplicate stop '{name}' repeated multiple times in this CSV.");
            else
                seenNamesInFile.Add(normName);
        }

        if (!string.IsNullOrEmpty(code))
        {
            if (seenCodesInFile.Contains(code))
                item.Errors.Add($"Duplicate stop code '{code}' repeated in this CSV.");
            else
                seenCodesInFile.Add(code);
        }

        // Deduplication against existing database records (by Code or by Name/phonetics)
        Stop? match = null;
        if (!string.IsNullOrEmpty(code) && dbStopCodes.Contains(code))
        {
            match = dbStops.FirstOrDefault(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null && !string.IsNullOrEmpty(normName))
        {
            if (dbStopNames.TryGetValue(normName, out var nameMatch))
            {
                match = nameMatch;
            }
            else
            {
                match = dbStops.FirstOrDefault(s => StopNormalizer.AreSameStop(s.Name, name, s.City, city));
            }
        }

        if (match is not null)
        {
            item.Status = "Reused";
            item.Warnings.Add($"Stop '{name}' matches existing stop '{match.Name}' (Code: {match.Code}) and will be safely reused without duplicating.");
            return;
        }

        item.Status = item.Errors.Count > 0 ? "Error" : item.Warnings.Count > 0 ? "Warning" : "Ready";
    }

    private void ValidateBusRow(
        Dictionary<string, string> dict,
        BulkRowResult item,
        HashSet<string> dbFleetNums,
        HashSet<string> dbBusRegs,
        HashSet<string> seenFleetInFile,
        HashSet<string> seenRegInFile,
        HashSet<string> dbOpCodes,
        HashSet<string> fileOpCodes,
        HashSet<string> dbDrvCodes,
        HashSet<string> fileDrvCodes)
    {
        var fleet = dict.GetValueOrDefault("fleetNumber")?.Trim().ToUpperInvariant() ?? "";
        var reg = dict.GetValueOrDefault("registrationNumber")?.Trim().ToUpperInvariant() ?? "";
        var capStr = dict.GetValueOrDefault("capacity")?.Trim() ?? "";
        var opCode = (dict.GetValueOrDefault("operatorCode") ?? dict.GetValueOrDefault("operatorId") ?? "").Trim().ToUpperInvariant();
        var drvCode = (dict.GetValueOrDefault("assignedDriverCode") ?? dict.GetValueOrDefault("assignedDriverId") ?? "").Trim().ToUpperInvariant();

        item.Name = $"{fleet} ({reg})";
        item.Code = fleet;

        if (string.IsNullOrEmpty(fleet)) item.Errors.Add("Bus Fleet Number is required.");
        if (string.IsNullOrEmpty(reg)) item.Errors.Add("Bus Registration Number is required.");

        if (!decimal.TryParse(capStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var capDec)
            || capDec < 1 || capDec > 120 || decimal.Floor(capDec) != capDec)
        {
            item.Errors.Add("Bus capacity must be a whole number between 1 and 120.");
        }

        if (string.IsNullOrEmpty(opCode))
        {
            item.Errors.Add("Operator is required for a bus. Provide operatorCode or operatorId.");
        }
        else if (!dbOpCodes.Contains(opCode) && !fileOpCodes.Contains(opCode))
        {
            item.Errors.Add($"Referenced operator '{opCode}' was not found in the database or this CSV.");
        }

        if (!string.IsNullOrEmpty(drvCode))
        {
            var normDrv = NormalizeDriverCode(drvCode);
            if (!dbDrvCodes.Contains(drvCode) && !fileDrvCodes.Contains(drvCode) &&
                !dbDrvCodes.Contains(normDrv) && !fileDrvCodes.Contains(normDrv))
            {
                item.Warnings.Add($"Referenced driver '{drvCode}' not found. Bus will remain unassigned.");
            }
        }

        if (!string.IsNullOrEmpty(fleet))
        {
            if (seenFleetInFile.Contains(fleet)) item.Errors.Add($"Fleet number '{fleet}' is repeated in this CSV.");
            else seenFleetInFile.Add(fleet);
        }

        if (!string.IsNullOrEmpty(reg))
        {
            if (seenRegInFile.Contains(reg)) item.Errors.Add($"Registration '{reg}' is repeated in this CSV.");
            else seenRegInFile.Add(reg);
        }

        if (dbFleetNums.Contains(fleet) || dbBusRegs.Contains(reg))
        {
            item.Status = "Reused";
            item.Warnings.Add($"Bus '{fleet}' ({reg}) already exists in database and will be safely preserved.");
            return;
        }

        item.Status = item.Errors.Count > 0 ? "Error" : item.Warnings.Count > 0 ? "Warning" : "Ready";
    }

    #endregion

    #region Commit Execution

    private async Task<BulkCommitResult> ExecuteCommitAsync(BulkImportSession session, bool updateExisting, CancellationToken ct)
    {
        var result = new BulkCommitResult();
        var validRows = session.Rows.Where(r => r.Errors.Count == 0 && r.Status != "Error").ToList();

        var operatorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var driverMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stopMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var busMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Preload existing DB records into maps
        var existingOps = await _db.Operators.ToListAsync(ct);
        foreach (var op in existingOps) operatorMap[op.Code.Trim()] = op.Id;

        var existingDrvs = await _db.Drivers.Include(d => d.User).ToListAsync(ct);
        foreach (var drv in existingDrvs)
        {
            if (!string.IsNullOrEmpty(drv.User.DriverCode))
                driverMap[drv.User.DriverCode.Trim()] = drv.Id;
        }

        var existingStops = await _db.Stops.ToListAsync(ct);
        foreach (var s in existingStops) stopMap[s.Code.Trim()] = s.Id;

        var existingBuses = await _db.Buses.ToListAsync(ct);
        foreach (var b in existingBuses) busMap[b.FleetNumber.Trim()] = b.Id;

        // Phase 1: Operators
        var opRows = validRows.Where(r => string.Equals(r.Type, "operators", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Type, "operator", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in opRows)
        {
            var code = r.Code.Trim();
            if (operatorMap.TryGetValue(code, out var existingOpId))
            {
                if (updateExisting)
                {
                    var existingOp = existingOps.FirstOrDefault(o => o.Id == existingOpId);
                    if (existingOp is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(r.Name)) existingOp.Name = r.Name.Trim();
                        var cn = Blank(r.Data.GetValueOrDefault("contactName"));
                        if (cn is not null) existingOp.ContactName = cn;
                        var ph = Blank(r.Data.GetValueOrDefault("phone"));
                        if (ph is not null) existingOp.Phone = ph;
                        var em = Blank(r.Data.GetValueOrDefault("email"));
                        if (em is not null) existingOp.Email = em;
                        r.Status = "Updated";
                        result.Updated++;
                        continue;
                    }
                }
                r.Status = "Reused";
                result.Reused++;
                continue;
            }

            try
            {
                var op = new Operator
                {
                    Name = r.Name.Trim(),
                    Code = code.ToUpperInvariant(),
                    ContactName = Blank(r.Data.GetValueOrDefault("contactName")),
                    Phone = Blank(r.Data.GetValueOrDefault("phone")),
                    Email = Blank(r.Data.GetValueOrDefault("email")),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Operators.Add(op);
                await _db.SaveChangesAsync(ct);
                operatorMap[code] = op.Id;
                r.Status = "Imported";
                result.Imported++;
            }
            catch
            {
                r.Status = "Failed";
                result.Failed++;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Phase 2: Drivers
        var drvRows = validRows.Where(r => string.Equals(r.Type, "drivers", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Type, "driver", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in drvRows)
        {
            var code = r.Code.Trim();
            if (driverMap.TryGetValue(code, out var existingDrvId))
            {
                if (updateExisting)
                {
                    var existingDrv = existingDrvs.FirstOrDefault(d => d.Id == existingDrvId);
                    if (existingDrv is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(r.Name)) existingDrv.User.FullName = r.Name.Trim();
                        var ph = Blank(r.Data.GetValueOrDefault("phone"));
                        if (ph is not null) existingDrv.User.Phone = ph;
                        var lic = Blank(r.Data.GetValueOrDefault("licenseNumber"));
                        if (lic is not null) existingDrv.LicenseNumber = lic;
                        var opCode = (r.Data.GetValueOrDefault("operatorCode") ?? r.Data.GetValueOrDefault("operatorId") ?? "").Trim();
                        if (!string.IsNullOrEmpty(opCode) && operatorMap.TryGetValue(opCode, out var foundOpId))
                            existingDrv.OperatorId = foundOpId;
                        r.Status = "Updated";
                        result.Updated++;
                        continue;
                    }
                }
                r.Status = "Reused";
                result.Reused++;
                continue;
            }

            try
            {
                var opCode = (r.Data.GetValueOrDefault("operatorCode") ?? r.Data.GetValueOrDefault("operatorId") ?? "").Trim();
                int? opId = null;
                if (!string.IsNullOrEmpty(opCode) && operatorMap.TryGetValue(opCode, out var foundOpId))
                    opId = foundOpId;

                var user = new User
                {
                    Id = Guid.NewGuid(),
                    FullName = r.Name.Trim(),
                    Email = (r.Data.GetValueOrDefault("email") ?? "").Trim(),
                    Phone = Blank(r.Data.GetValueOrDefault("phone")),
                    Role = "Driver",
                    DriverCode = code.ToUpperInvariant(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                var pass = r.Data.GetValueOrDefault("password")?.Trim();
                if (string.IsNullOrEmpty(pass)) pass = "Driver@12345";
                user.PasswordHash = _passwords.HashPassword(user, pass);
                _db.Users.Add(user);

                var driver = new Driver
                {
                    User = user,
                    LicenseNumber = (r.Data.GetValueOrDefault("licenseNumber") ?? "DL-PENDING").Trim(),
                    OperatorId = opId,
                    IsActive = true
                };
                _db.Drivers.Add(driver);
                await _db.SaveChangesAsync(ct);
                driverMap[code] = driver.Id;
                r.Status = "Imported";
                result.Imported++;
            }
            catch
            {
                r.Status = "Failed";
                result.Failed++;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Phase 3: Stops
        var stopRows = validRows.Where(r => string.Equals(r.Type, "stops", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Type, "stop", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in stopRows)
        {
            var code = r.Code.Trim().ToUpperInvariant();
            var name = r.Name.Trim();

            // 1. Direct code match
            if (stopMap.TryGetValue(code, out var existingId))
            {
                if (updateExisting)
                {
                    var existingStop = existingStops.FirstOrDefault(s => s.Id == existingId);
                    if (existingStop is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(name)) existingStop.Name = name;
                        var latStr = r.Data.GetValueOrDefault("latitude")?.Trim();
                        var lonStr = r.Data.GetValueOrDefault("longitude")?.Trim();
                        if (decimal.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pLat)) existingStop.Latitude = pLat;
                        if (decimal.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pLon)) existingStop.Longitude = pLon;
                        var addr = Blank(r.Data.GetValueOrDefault("address"));
                        if (addr is not null) existingStop.Address = addr;
                        var city = Blank(r.Data.GetValueOrDefault("city"));
                        if (city is not null) existingStop.City = city;
                        r.Status = "Updated";
                        result.Updated++;
                        continue;
                    }
                }
                r.Status = "Reused";
                result.Reused++;
                continue;
            }

            // 2. Name or phonetic / transliteration match (e.g. Dhangar vs Dhanger)
            var existingByName = existingStops.FirstOrDefault(s =>
                s.IsActive && StopNormalizer.AreSameStop(s.Name, name, s.City, r.Data.GetValueOrDefault("city")));

            if (existingByName is not null)
            {
                if (updateExisting)
                {
                    var latStr = r.Data.GetValueOrDefault("latitude")?.Trim();
                    var lonStr = r.Data.GetValueOrDefault("longitude")?.Trim();
                    if (decimal.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pLat)) existingByName.Latitude = pLat;
                    if (decimal.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pLon)) existingByName.Longitude = pLon;
                    var addr = Blank(r.Data.GetValueOrDefault("address"));
                    if (addr is not null) existingByName.Address = addr;
                    r.Status = "Updated";
                    result.Updated++;
                    stopMap[code] = existingByName.Id;
                    continue;
                }
                r.Status = "Reused";
                result.Reused++;
                stopMap[code] = existingByName.Id;
                stopMap[existingByName.Code.Trim().ToUpperInvariant()] = existingByName.Id;
                continue;
            }

            try
            {
                var latStr = r.Data.GetValueOrDefault("latitude")?.Trim();
                var lonStr = r.Data.GetValueOrDefault("longitude")?.Trim();
                var lat = decimal.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pLat) ? pLat : 29.5155m;
                var lon = decimal.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pLon) ? pLon : 75.4553m;

                var stop = new Stop
                {
                    Name = r.Name.Trim(),
                    Code = code,
                    Latitude = lat,
                    Longitude = lon,
                    Address = Blank(r.Data.GetValueOrDefault("address")),
                    City = Blank(r.Data.GetValueOrDefault("city")),
                    IsActive = true
                };
                _db.Stops.Add(stop);
                await _db.SaveChangesAsync(ct);
                stopMap[code] = stop.Id;
                r.Status = "Imported";
                result.Imported++;
            }
            catch
            {
                r.Status = "Failed";
                result.Failed++;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Phase 4: Buses
        var busRows = validRows.Where(r => string.Equals(r.Type, "buses", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Type, "bus", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in busRows)
        {
            var fleet = (r.Data.GetValueOrDefault("fleetNumber") ?? "").Trim().ToUpperInvariant();
            var reg = (r.Data.GetValueOrDefault("registrationNumber") ?? "").Trim().ToUpperInvariant();

            if (busMap.TryGetValue(fleet, out var existingBusId))
            {
                if (updateExisting)
                {
                    var existingBus = existingBuses.FirstOrDefault(b => b.Id == existingBusId);
                    if (existingBus is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(reg)) existingBus.RegistrationNumber = reg;
                        var mdl = Blank(r.Data.GetValueOrDefault("model"));
                        if (mdl is not null) existingBus.Model = mdl;
                        if (int.TryParse(r.Data.GetValueOrDefault("capacity"), out var cap) && cap > 0) existingBus.Capacity = cap;
                        var bt = Blank(r.Data.GetValueOrDefault("busType"));
                        if (bt is not null) existingBus.BusType = bt;
                        var opCode = (r.Data.GetValueOrDefault("operatorCode") ?? r.Data.GetValueOrDefault("operatorId") ?? "").Trim();
                        if (!string.IsNullOrEmpty(opCode) && operatorMap.TryGetValue(opCode, out var foundOpId))
                            existingBus.OperatorId = foundOpId;
                        r.Status = "Updated";
                        result.Updated++;
                        continue;
                    }
                }
                r.Status = "Reused";
                result.Reused++;
                continue;
            }

            try
            {
                var opCode = (r.Data.GetValueOrDefault("operatorCode") ?? r.Data.GetValueOrDefault("operatorId") ?? "").Trim();
                int opId = operatorMap.TryGetValue(opCode, out var foundOpId) ? foundOpId : operatorMap.Values.FirstOrDefault();

                if (opId == 0)
                {
                    r.Status = "Failed";
                    result.Failed++;
                    continue;
                }

                var drvCode = (r.Data.GetValueOrDefault("assignedDriverCode") ?? r.Data.GetValueOrDefault("assignedDriverId") ?? "").Trim();
                int? drvId = null;
                if (!string.IsNullOrEmpty(drvCode))
                {
                    if (driverMap.TryGetValue(drvCode, out var directId))
                        drvId = directId;
                    else if (driverMap.TryGetValue(NormalizeDriverCode(drvCode), out var normId))
                        drvId = normId;
                }

                decimal.TryParse(r.Data.GetValueOrDefault("capacity")?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var capDec);
                var cap = (int)capDec;
                if (cap <= 0) cap = 40;

                var bus = new Bus
                {
                    FleetNumber = fleet,
                    RegistrationNumber = reg,
                    Model = Blank(r.Data.GetValueOrDefault("model")) ?? "Standard Bus",
                    Capacity = cap,
                    BusType = Blank(r.Data.GetValueOrDefault("busType")) ?? "Ordinary",
                    OperatorId = opId,
                    AssignedDriverId = drvId,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Buses.Add(bus);
                await _db.SaveChangesAsync(ct);
                busMap[fleet] = bus.Id;
                r.Status = "Imported";
                result.Imported++;

                // Optional: Extract and build route & schedule if route data is present in bus row
                var routeDetails = ExtractRouteDetails(r.Data);
                if (routeDetails.HasValue)
                {
                    var (routeTitle, stopNames, departureTime, serviceDays) = routeDetails.Value;
                    var createdRouteId = await EnsureRouteAsync(routeTitle, stopNames, opId, stopMap, ct);
                    if (createdRouteId > 0)
                    {
                        result.Routes++;
                        if (TimeOnly.TryParse(departureTime, out var dTime))
                        {
                            var schedExists = await _db.Schedules.AnyAsync(s => s.RouteId == createdRouteId && s.BusId == bus.Id && s.DepartureTime == dTime, ct);
                            if (!schedExists)
                            {
                                var schedule = new Schedule
                                {
                                    RouteId = createdRouteId,
                                    BusId = bus.Id,
                                    DriverId = drvId,
                                    DepartureTime = dTime,
                                    ServiceDays = serviceDays,
                                    IsActive = true
                                };
                                _db.Schedules.Add(schedule);
                                await _db.SaveChangesAsync(ct);
                                result.Schedules++;
                            }
                        }
                    }
                }
            }
            catch
            {
                r.Status = "Failed";
                result.Failed++;
            }
        }

        // Phase 5: Mark skipped rows
        var skippedCount = session.Rows.Count(r => r.Errors.Count > 0);
        foreach (var r in session.Rows.Where(r => r.Errors.Count > 0))
            r.Status = "Skipped";

        result.Skipped = skippedCount;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    private async Task<int> EnsureRouteAsync(
        string routeTitle,
        List<string> stopNames,
        int operatorId,
        Dictionary<string, int> stopMap,
        CancellationToken ct)
    {
        var existingRoute = await _db.Routes.FirstOrDefaultAsync(r => r.Name == routeTitle && r.IsActive, ct);
        if (existingRoute is not null) return existingRoute.Id;

        var stopIds = new List<int>();
        var allActiveStops = await _db.Stops.Where(s => s.IsActive).ToListAsync(ct);
        foreach (var name in stopNames)
        {
            var match = allActiveStops.FirstOrDefault(s => StopNormalizer.AreSameStop(s.Name, name));
            if (match is not null)
            {
                stopIds.Add(match.Id);
            }
            else
            {
                var cleanCode = "S-" + Regex.Replace(name, @"[^A-Za-z0-9]", "").ToUpperInvariant();
                if (cleanCode.Length > 20) cleanCode = cleanCode[..20];
                var newStop = new Stop
                {
                    Name = name,
                    Code = cleanCode,
                    Latitude = 29.5155m,
                    Longitude = 75.4553m,
                    Address = "Depot point",
                    City = "Fatehabad",
                    IsActive = true
                };
                _db.Stops.Add(newStop);
                await _db.SaveChangesAsync(ct);
                allActiveStops.Add(newStop);
                stopIds.Add(newStop.Id);
                stopMap[newStop.Code] = newStop.Id;
            }
        }

        if (stopIds.Count < 2) return 0;

        var routeCode = "R-" + Regex.Replace(routeTitle, @"[^A-Za-z0-9]", "").ToUpperInvariant();
        if (routeCode.Length > 20) routeCode = routeCode[..20];

        var route = new BusRoute
        {
            Name = routeTitle,
            Code = routeCode,
            OperatorId = operatorId,
            DistanceMode = "Auto",
            IsActive = true
        };
        _db.Routes.Add(route);
        await _db.SaveChangesAsync(ct);

        for (int i = 0; i < stopIds.Count; i++)
        {
            _db.RouteStops.Add(new RouteStop
            {
                RouteId = route.Id,
                StopId = stopIds[i],
                SequenceNo = i + 1,
                DistanceFromOriginKm = 0
            });
        }
        await _db.SaveChangesAsync(ct);

        try
        {
            await _geometry.RebuildAsync(route.Id, ct);
        }
        catch { }

        return route.Id;
    }

    private static (string Title, List<string> Stops, string DepartureTime, string ServiceDays)? ExtractRouteDetails(Dictionary<string, string> dict)
    {
        var address = dict.GetValueOrDefault("address") ?? "";
        var name = dict.GetValueOrDefault("name") ?? "";

        var routeMatch = Regex.Match(address, @"Route:\s*([^;]+)", RegexOptions.IgnoreCase);
        string? rawRoute = routeMatch.Success ? routeMatch.Groups[1].Value.Trim() : null;

        if (string.IsNullOrEmpty(rawRoute))
        {
            var toMatch = Regex.Match(name, @"^(.*)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
            if (toMatch.Success) rawRoute = name.Trim();
        }

        if (string.IsNullOrEmpty(rawRoute)) return null;

        var endpoints = Regex.Match(rawRoute, @"^(.*?)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
        if (!endpoints.Success) return null;

        var origin = endpoints.Groups[1].Value.Trim();
        var destination = endpoints.Groups[2].Value.Trim();
        if (origin.Length < 2 || destination.Length < 2) return null;

        var viaMatch = Regex.Match(address, @"Via:\s*([^;]+)", RegexOptions.IgnoreCase);
        var viaList = new List<string>();
        if (viaMatch.Success)
        {
            viaList = viaMatch.Groups[1].Value
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 1)
                .ToList();
        }

        var stops = new List<string> { origin };
        stops.AddRange(viaList);
        stops.Add(destination);

        var depMatch = Regex.Match(address, @"Departure:\s*([0-9]{1,2}:[0-9]{2})", RegexOptions.IgnoreCase);
        var departure = depMatch.Success ? depMatch.Groups[1].Value : "08:00";
        if (departure.Length == 4 && departure[1] == ':') departure = "0" + departure;

        var daysMatch = Regex.Match(address, @"Service days:\s*([^;]+)", RegexOptions.IgnoreCase);
        var days = (daysMatch.Success && daysMatch.Groups[1].Value.Trim().ToLowerInvariant().StartsWith("one"))
            ? "1000000"
            : "1111111";

        return (rawRoute, stops, departure, days);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (string FileName, string Content) GetTemplateContent(string type)
    {
        switch (type)
        {
            case "operators":
                return ("operators-template.csv",
                    "name,code,contactName,phone,email\r\n" +
                    "\"Haryana Roadways\",\"HR-ROADS\",\"Satish Kumar\",\"9876543210\",\"satish@hr-roadways.in\"\r\n" +
                    "\"Hisar Express Lines\",\"HISAR-EXP\",\"Rajesh Verma\",\"9812345678\",\"contact@hisarexpress.com\"\r\n");

            case "drivers":
                return ("drivers-template.csv",
                    "fullName,driverCode,email,phone,password,licenseNumber,operatorCode\r\n" +
                    "\"Rahul Sharma\",\"DRV-RAHUL\",\"rahul@hr-roadways.in\",\"9876543211\",\"Driver@12345\",\"HR-2023-998811\",\"HR-ROADS\"\r\n" +
                    "\"Amit Singh\",\"DRV-AMIT\",\"amit@hr-roadways.in\",\"9876543212\",\"Driver@12345\",\"HR-2022-445566\",\"HR-ROADS\"\r\n");

            case "stops":
                return ("stops-template.csv",
                    "name,code,latitude,longitude,address,city\r\n" +
                    "\"Fatehabad Depot\",\"FB-DEPOT\",\"29.5155\",\"75.4553\",\"Near Bus Stand, NH9\",\"Fatehabad\"\r\n" +
                    "\"Agroha Dham\",\"AGROHA-01\",\"29.3524\",\"75.6148\",\"Main Gate, Agroha\",\"Hisar\"\r\n" +
                    "\"Hisar Junction\",\"HSR-JUNC\",\"29.1539\",\"75.7229\",\"Railway Road\",\"Hisar\"\r\n");

            case "buses":
                return ("buses-template.csv",
                    "fleetNumber,registrationNumber,model,capacity,busType,operatorCode,assignedDriverCode\r\n" +
                    "\"HR39-1020\",\"HR-39-A-1020\",\"Tata Starbus Ultra\",\"40\",\"Ordinary\",\"HR-ROADS\",\"DRV-RAHUL\"\r\n" +
                    "\"HR39-1021\",\"HR-39-A-1021\",\"Ashok Leyland Viking\",\"52\",\"Semi-Deluxe\",\"HR-ROADS\",\"DRV-AMIT\"\r\n");

            case "all":
            default:
                return ("all-in-one-template.csv",
                    "type,name,code,contactName,phone,email,fullName,driverCode,password,licenseNumber,operatorCode,fleetNumber,registrationNumber,model,capacity,busType,assignedDriverCode,latitude,longitude,address,city\r\n" +
                    "\"operator\",\"Haryana Roadways\",\"HR-ROADS\",\"Satish Kumar\",\"9876543210\",\"satish@hr-roadways.in\",,,,,,,,,,,,,,\r\n" +
                    "\"driver\",,,,,,,\"Rahul Sharma\",\"DRV-RAHUL\",\"Driver@12345\",\"HR-2023-998811\",\"HR-ROADS\",,,,,,,,,,\r\n" +
                    "\"stop\",\"Fatehabad Depot\",\"FB-DEPOT\",,,,,,,,,,,,,,,\"29.5155\",\"75.4553\",\"Near Bus Stand, NH9\",\"Fatehabad\"\r\n" +
                    "\"bus\",,,,,,,,,,\"HR-ROADS\",\"HR39-1020\",\"HR-39-A-1020\",\"Tata Starbus Ultra\",\"40\",\"Ordinary\",\"DRV-RAHUL\",,,,\r\n");
        }
    }

    private static string DetectRowType(Dictionary<string, string> dict, string defaultKind)
    {
        if (dict.TryGetValue("type", out var typeVal) && !string.IsNullOrWhiteSpace(typeVal))
            return typeVal.Trim();

        return defaultKind;
    }

    private static Dictionary<string, string> RowToDictionary(List<string> headers, List<string> row)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            var val = i < row.Count ? row[i] : "";
            dict[headers[i]] = val;
        }
        return dict;
    }

    public static string SanitizeCsvCell(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        // Prevent CSV Formula Injection (=, @, +, or - followed by non-numeric formula expressions)
        if (trimmed.Length > 0 && (trimmed[0] == '=' || trimmed[0] == '@' || trimmed[0] == '+'))
        {
            return "'" + trimmed;
        }
        if (trimmed.Length > 1 && trimmed[0] == '-')
        {
            // Neutralize formula expressions starting with minus unless it is a valid numeric value
            if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return "'" + trimmed;
            }
        }
        return trimmed;
    }

    private static List<List<string>> ParseCsvLines(string text)
    {
        var rows = new List<List<string>>();
        var curRow = new List<string>();
        var curCell = new StringBuilder();
        var insideQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (insideQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        curCell.Append('"');
                        i++;
                    }
                    else
                    {
                        insideQuotes = false;
                    }
                }
                else
                {
                    curCell.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    insideQuotes = true;
                }
                else if (c == ',' || c == '\t' || c == ';')
                {
                    curRow.Add(SanitizeCsvCell(curCell.ToString()));
                    curCell.Clear();
                }
                else if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    curRow.Add(SanitizeCsvCell(curCell.ToString()));
                    curCell.Clear();
                    if (curRow.Any(cell => cell.Length > 0)) rows.Add(curRow);
                    curRow = new List<string>();
                }
                else if (c == '\n')
                {
                    curRow.Add(SanitizeCsvCell(curCell.ToString()));
                    curCell.Clear();
                    if (curRow.Any(cell => cell.Length > 0)) rows.Add(curRow);
                    curRow = new List<string>();
                }
                else
                {
                    curCell.Append(c);
                }
            }
        }

        if (curCell.Length > 0 || curRow.Count > 0)
        {
            curRow.Add(SanitizeCsvCell(curCell.ToString()));
            if (curRow.Any(cell => cell.Length > 0)) rows.Add(curRow);
        }

        return rows;
    }

    #endregion
}

public class CsvUploadForm
{
    public IFormFile? File { get; set; }
    public string? Type { get; set; }
}

public class BulkImportSession
{
    public string ImportId { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string Type { get; set; } = "all";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string AdminName { get; set; } = "";
    public string Status { get; set; } = "Validated";
    public int TotalRows { get; set; }
    public int ReadyCount { get; set; }
    public int ReusedCount { get; set; }
    public int InvalidCount { get; set; }
    public int WarningCount { get; set; }
    public List<BulkRowResult> Rows { get; set; } = new();
    public BulkCommitResult? CommitResult { get; set; }
}

public class BulkRowResult
{
    public int Row { get; set; }
    public string Type { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Ready";
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, string> Data { get; set; } = new();
}

public class BulkCommitResult
{
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Reused { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Deleted { get; set; }
    public int Routes { get; set; }
    public int Schedules { get; set; }
    public DateTime CompletedAt { get; set; }
}
