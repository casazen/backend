# Ticino Police Notification Format

**Source:** notifiche.ti.ch official documentation  
**Verified:** 2026-10-25  
**Format Version:** 1.0  
**Regulation:** Canton Ticino police notification requirement for overnight stays

## Overview

This document describes the official XML format for daily police notifications required for properties in Canton Ticino (TI), Switzerland.

## Legal Basis

- Swiss Federal Law on Accommodation Statistics
- Canton Ticino Regulation RL 942.120 art. 6
- ADR-016 compliance file formats

## XML Format

### Daily Notification Structure

```xml
<?xml version="1.0" encoding="UTF-8"?>
<TicinoNotification>
  <Header>
    <ReportDate>YYYY-MM-DD</ReportDate>
    <PropertyId>GUID</PropertyId>
    <PropertyName>String</PropertyName>
    <PropertyAddress>String</PropertyAddress>
    <MunicipalityCode>BFS Number</MunicipalityCode>
  </Header>
  <Stays>
    <Stay>
      <BookingId>GUID</BookingId>
      <GuestName>Full Name</GuestName>
      <CheckIn>YYYY-MM-DD</CheckIn>
      <CheckOut>YYYY-MM-DD</CheckOut>
      <NumberOfGuests>Integer</NumberOfGuests>
    </Stay>
    <!-- Additional stays -->
  </Stays>
</TicinoNotification>
```

## Field Descriptions

| Field | Type | Required | Description |
|---|---|---|---|
| ReportDate | Date | Yes | Report generation date (Europe/Zurich) |
| PropertyId | GUID | Yes | Unique property identifier |
| PropertyName | String | Yes | Property name |
| PropertyAddress | String | Yes | Full property address |
| MunicipalityCode | String | Yes | Official BFS municipality number |
| BookingId | GUID | Yes | Unique booking identifier |
| GuestName | String | Yes | Guest full name |
| CheckIn | Date | Yes | Check-in date |
| CheckOut | Date | Yes | Check-out date |
| NumberOfGuests | Integer | Yes | Total guests in booking |

## Submission Requirements

- Daily submission required for all overnight stays
- Submission deadline: By end of day following the overnight stay
- Electronic submission via notifiche.ti.ch portal
- Retention: Receipts must be archived for 10 years

## Privacy Requirements

Per RL 942.120 art. 6:
- Guest document images MUST NOT be stored for Ticino properties
- Only extracted data may be retained
- Data processing must be in-memory only

## References

- Official portal: https://notifiche.ti.ch
- BFS municipality codes: https://www.bfs.admin.ch/bfs/de/home/grundlagen/agvch.html
- Canton Ticino tourism regulation: RL 942.120
