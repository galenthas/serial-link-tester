# Measurement definitions

The application tests a transparent binary byte stream using two ports on one
Windows PC, or one port with an external loopback. It does not require a special
protocol handler inside the link under test.

## Test packets

Every packet contains a run ID, sequence number, payload length, independently
protected header, deterministic pseudo-random payload and payload CRC32. COBS
encoding and a zero delimiter allow recovery after insertion or deletion of bytes.
The protocol adds 40 bytes before encoding. Payload sizes range from 1 to 2048 bytes.

## Integrity and throughput

Throughput mode schedules each packet according to its actual encoded length,
baud rate and target load. With 8N1, one serial byte occupies ten line bits.
The sender does not compensate scheduling delays with catch-up bursts.

```
target interval = encoded bytes × 10 / baud / (load percent / 100)
```

In bidirectional mode this target applies to each direction independently. Both
ports are opened once; each direction has its own run ID, sequence and measurements.
Latency mode allows one unresolved probe per direction until response or timeout,
then waits at least 20 ms before sending the next probe.

## Results

| Result | Definition |
|---|---|
| Missing | Completed writes without an identifiable response when the final receive window closes. During a run these are pending. |
| Corrupt payload | Recognized packet whose payload CRC or deterministic pattern fails. |
| Invalid packet format | Unreadable header, invalid encoding or truncated/oversized packet. A damaged header can also leave a packet missing; do not sum the two counts. |
| Active goodput | Valid payload bits after the first valid packet divided by the first-to-last valid reception interval. Requires at least two valid packets. |
| Whole-session average | All valid payload bits divided by total session duration, including setup and the final receive window. |
| Latency | PC write submission to completion of the read that contains the end of a valid packet. |
| Jitter | Mean absolute difference between consecutive valid packet delays in receive order. |
| Percentiles | Upper edges of 0.1 ms histogram bins; delays at or above 60 seconds use an overflow indication. |

For example, 115200 baud permits 92.16 kbit/s of byte data with 8N1. A 1500-byte
payload occupies approximately 1542–1548 encoded bytes, giving an ideal payload
goodput of about 89.3–89.6 kbit/s before pacing gaps. Baud rate and goodput measure
different things.

## Timing scope

Transmit and receive timestamps use the same PC's high-resolution counter. Reads
and writes run independently of the GUI using native overlapped Windows I/O.
The receive configuration returns available data without waiting for the entire
read buffer to fill. A high-resolution waitable timer paces transmission when
supported by Windows.

Timing still includes host scheduling, driver/USB buffers, UART serialization and
the device or link under test. The application does not provide hardware timestamps,
clock synchronization between separate PCs or physical-layer bit error rate.
Round-trip results cannot establish one-way latency by simply dividing by two.

## Serial warnings and test-stopping errors

Framing, Break, Overrun, RX overflow and Parity are named non-fatal serial warnings.
Their counts describe observations reported by the port, not affected bytes.
Warnings remain as cumulative history until a new test starts. They are distinct
from protocol packet-format failures and from I/O exceptions that stop a test.
Unknown warning flags retain their hexadecimal values.

In bidirectional mode, port status is sampled by both directions on shared physical
endpoints. These observations are not uniquely attributable directional fault counts.

## Stop, abort and validation

Stop ends transmission while preserving each receive window. The default response
threshold and final receive window are 1000 ms; increase this for slow baud rates,
large payloads or long-delay links. Aborting or losing a port leaves the test incomplete.
Do not attribute an incomplete host-side test solely to the link.

The automated suite exercises protocol integrity, accounting, concurrent transport,
cancellation, warnings, failures and exports using simulated transports. Native
timer creation and invalid-port handling are also checked. These tests do not
validate physical hardware timing or cable disconnect/reconnect behavior.
