using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusTrack.Api.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly TelemetryService _telemetry;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(TelemetryService telemetry, ILogger<TelemetryController> logger)
    {
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Ingests real-time telemetry from IoT hardware units (OBUs, GPS trackers)
    /// conforming to the EU Horizon TELEMETRY security framework.
    /// </summary>
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] IoTTelemetryPacket packet, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packet.DeviceUid))
            return BadRequest(new { message = "DeviceUid is required." });

        if (string.IsNullOrWhiteSpace(packet.FleetNumber))
            return BadRequest(new { message = "FleetNumber is required." });

        var result = await _telemetry.IngestPacketAsync(packet, ct);

        if (!result.Accepted)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns the live registry of all active IoT devices, their trust scores, and operational states.
    /// </summary>
    [HttpGet("devices")]
    [Authorize(Roles = "Admin")]
    public IActionResult GetDevices()
    {
        var devices = _telemetry.GetDevices();
        return Ok(devices);
    }

    /// <summary>
    /// Returns recent cybersecurity anomalies detected by the TELEMETRY anomaly detection engine.
    /// </summary>
    [HttpGet("anomalies")]
    [Authorize(Roles = "Admin")]
    public IActionResult GetAnomalies([FromQuery] int count = 50)
    {
        var limit = Math.Clamp(count, 1, 200);
        var anomalies = _telemetry.GetRecentAnomalies(limit);
        return Ok(anomalies);
    }

    /// <summary>
    /// Returns aggregate telemetry and IoT cybersecurity stats for the system.
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Roles = "Admin")]
    public IActionResult GetStats()
    {
        var stats = _telemetry.GetStats();
        return Ok(stats);
    }

    /// <summary>
    /// Admin action to reset an IoT device's trust score following manual inspection or recalibration.
    /// </summary>
    [HttpPost("devices/{deviceUid}/reset-trust")]
    [Authorize(Roles = "Admin")]
    public IActionResult ResetDeviceTrust(string deviceUid)
    {
        var success = _telemetry.ResetDeviceTrust(deviceUid);
        if (!success)
            return NotFound(new { message = "Device not found in active telematics registry." });

        return Ok(new { message = $"Trust score for device '{deviceUid}' has been restored to 1.0." });
    }
}
