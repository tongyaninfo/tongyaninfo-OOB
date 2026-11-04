This guide covers the current PC web application and desktop client of Tongyan Out-of-Band Management, supporting HP iLO4 and Dell iDRAC 8. Available pages, actions, and device capabilities depend on your permissions and the actual device response.

## Contents

- [Getting started](#getting-started)
- [Overview and history](#overview-and-history)
- [Server management](#server-management)
- [Configuration import and export](#configuration-import-and-export)
- [Server details](#server-details)
- [UID and power operations](#uid-and-power-operations)
- [Consoles and virtual media](#consoles-and-virtual-media)
- [Users and accounts](#users-and-accounts)
- [Mail and operation reports](#mail-and-operation-reports)
- [Theme and language](#theme-and-language)
- [Exception logs and troubleshooting](#exception-logs-and-troubleshooting)

## Getting started

1. Sign in with the system account and password assigned by your administrator. The overview opens by default. System accounts and device BMC accounts are maintained separately.
2. With server management permission, open Servers, add a device, enter its BMC address, credentials, and sampling settings, then save.
3. Test the connection, then open the server details to refresh hardware inventory and inspect live readings and history.
4. For remote video, open Consoles, choose a server, and click Connect. Opening the page or selecting a device does not establish a console session automatically.

Ordinary accounts can view the overview, server list, and server details by default. User management, consoles, server management, and power operations are separate permissions. Mail and exception logs are restricted to system administrators. Contact an administrator if a page or action is missing.

## Overview and history

The overview shows CPU temperature, system power, fan speed, RAID controller temperature, and inlet/outlet temperature. The arrows on each card independently cycle between the aggregate and individual servers. Page-level arrows change the whole page scope. Hover over or focus a reading to see device details.

- Trends show the latest 15 minutes by default. Aggregate CPU, inlet, and outlet readings show maximum, average, and minimum values. Individual server charts retain separate CPU and sensor series; inlet lines are dashed and outlet lines are solid.
- Fans show RPM or percentage. Switch units to see the corresponding series and details. A missing unit is never calculated from another unit.
- Select the history icon at the top right of a card, then choose one day, one week, one month, or enter an Asia/Shanghai range and explicitly run the query. History follows the current card and server scope, spans at most 366 days, and uses stored data only.
- Select the overview fullscreen icon to expand the dashboard. Switching scope, details, and history remain available. Press Esc or use Exit to return.

The WebSocket indicator describes the live push connection. Disconnections, failed samples, and stale readings leave gaps. Reconnecting receives future samples without automatically filling gaps. Missing, unsupported, or stale readings do not mean zero.

## Server management

Select Add, enter a name, choose HP / iLO4 or DELL / iDRAC 8, and enter the BMC address, device username, and password. Sampling intervals range from 1 to 86400 seconds, with a default of 30 seconds. Enabled devices are sampled according to their saved settings.

Ignore HTTPS certificate errors is selected by default for new devices; choose the policy appropriate for the device certificate. A configured certificate fingerprint must still match. When editing, leave the password blank to retain it. Changes to the address, device account, or other credential identity fields may require re-entering credentials as indicated on the form. Changing only the certificate policy does not require re-entering the password.

Connection tests check device access and are separate from saving. Server details provide editing, inventory refresh, and manual sampling, all requiring server management permission. Sampling and inventory reads do not send power commands.

## Configuration import and export

- **Export configuration** exports all servers regardless of search or pagination. The file includes device accounts and passwords. Set a file password of 8–128 characters to create an encrypted file, and keep the file and password safe separately.
- **Import configuration** accepts an encrypted JSON file up to 4 MiB. Enter its file password and confirm. Devices match by BMC address and controller type: identical configurations are skipped, different configurations are overwritten, and unmatched devices are added. Servers absent from the file are retained.
- Overwrites retain the existing server identifier and monitoring history. The result lists added, overwritten, and skipped counts. If the outcome is unknown, refresh the list and verify before deciding what to do next; imports are not retried automatically.

## Server details

Open a server to inspect its model, serial number, BMC address, firmware, sampling time, and connection notices.

| Area | Use |
| --- | --- |
| Hardware inventory | Inspect CPUs, memory, batteries, power devices, and disks under controllers; refresh when permitted |
| Live data | Inspect device-provided power, temperature, and fan readings; sample manually when permitted |
| History | Select metrics and a time range to view charts and summaries by unit |
| Power operations | See available actions and records according to permissions, live capabilities, and the global switch |

Readings retain their units, sources, sampling times, and unsupported/missing status. RAID controller temperature uses explicit controller sensors rather than disk or battery temperatures. Displayed times use Asia/Shanghai by default.

## UID and power operations

UID helps locate a server and requires server management permission. Choose On, Blink, or Off according to device capabilities. Clicking sends the action immediately without a server-name confirmation; check the readback status afterward.

Power actions require the separate power operations permission and real power control enabled by the deployment administrator. The page first reads current capabilities. Select an action, enter the exact server name, and confirm.

- Power on, short power-button press, forced power off, graceful shutdown, and restart options depend on actual device support. Forced actions may interrupt a running system; verify the workload first.
- Device acceptance does not prove completion. For unknown or pending-verification results, query the original operation rather than creating the same action again.
- The original operator can record the actual result and supporting evidence. This manual review updates the record only and sends no device command.

Opening or refreshing pages and background sampling do not send power actions.

## Consoles and virtual media

Choose a server and click Connect. Up to four normal tabs and floating windows can be open in total. Unsupported firmware or vendor bridges display an unavailable notice; refreshing cannot add a capability the device does not provide.

Enable keyboard/mouse input and click the video to capture input. Clicking elsewhere or pressing Ctrl+Alt releases pressed keys and capture while leaving the input switch on; click the video again to resume. The on-screen keyboard sends Esc, function keys, and combinations. Chinese input methods and text paste are not supported.

In console fullscreen, Esc is sent to the remote server; use Ctrl+Alt to release input. HTTPS browsers supporting keyboard lock can provide full capture. Other environments may use page fullscreen and show limitations. Use the on-page exit control when needed; the browser retains long-press Esc as an emergency exit.

Use the detach button below the fullscreen icon to move the video into a floating window within the page. Drag, resize, minimize, or dock it again. Switching application menus retains the same connection. Closing a console tab, refreshing, or closing the browser page ends the session. Signing out or revoking console permission also clears sessions.

The virtual media drawer offers local images, the platform image library, or device-accessible image URLs according to device capabilities:

- Local mapping accepts a non-empty ISO up to 16 GiB without a complete upload first. Keep the current browser page and console session connected throughout use.
- The platform library supports ISO and 1.44 MB IMG files, with uploads up to 16 GiB. Mounting is unavailable until the platform image address and device access requirements are ready.
- Choose a virtual device and confirm the server name before mounting or ejecting. These actions insert/remove media without changing boot order or restarting the server.
- Closing the drawer does not eject media. URL/library mounts are independent of the console and remain when its tab closes. Local mapping depends on the browser session.
- Query the original media operation when the outcome is unknown. Only after verifying that it did not execute may the original operator record evidence and confirm non-execution. This review does not resend a mount or eject command.

Session traffic measures observable console and media payloads, with image-read traffic listed separately. Neither represents total server network-interface traffic.

## Users and accounts

With user management permission, add/edit users, enable/disable them, logically delete them, reset passwords separately, and assign each of the four permissions. Server management covers device configuration, connection tests, inventory refresh, manual sampling, and UID. Power operations covers power commands, records, and result reviews.

The system administrator flag does not replace these four permissions. Ordinary user managers cannot change system administrator accounts. Non-deleted usernames, including disabled accounts, are unique after trimming whitespace and ignoring case. Deleted usernames may be reused.

Choose Change password from the top-right avatar menu and enter the current password, new password, and confirmation. Passwords must contain 8–128 characters. Account, password, enabled-status, or permission changes invalidate previous sessions. The account settings page still contains template profile/security demonstrations whose saving is not connected; use the formal Change password action.

## Mail and operation reports

System administrators can open Mail and use the Sent mail, Mailbox settings, and Operation reports tabs.

1. Enter the SMTP server, sender email, and app password/password in Mailbox settings. Advanced settings default to port 465 and SSL/TLS; choose STARTTLS (usually port 587) when required by your mail provider. A blank login account uses the sender email; a blank password during editing retains the saved value.
2. Save settings. Saving neither connects nor sends mail. After saving, run Check connection or Send test email separately. Save any changes before these checks.
3. In Sent mail, choose Send email, enter one recipient, a subject, and a body, then confirm. Filter records or open their details to inspect results.

Accepted by the mail server does not mean delivered to the recipient. For unknown outcomes, query the original request. Retry that same request only when the page explicitly permits it. The system records its own outgoing mail and does not provide an inbox.

Configure recipients and Asia/Shanghai delivery times separately for daily, weekly, and monthly reports. Schedules are disabled by default, and saving does not send immediately. Reports cover the previous complete calendar day, week (Monday–Sunday), or month. Monthly schedules accept days 1–31; shorter months use their last day. After offline recovery, only the latest due period is processed, and existing records are not resent automatically.

For historical analysis, enter an Asia/Shanghai range up to 366 days, including the start and excluding the end. Generate preview and Download HTML read stored data without sending email. Sampling success rate describes collection results, not server uptime.

## Theme and language

Open appearance settings from the avatar theme action or the settings gear. Under Overall style, select Light, Dark, or Auto. Auto follows operating-system color-scheme changes; manual modes retain your selection. Settings are saved for the current account and restored after signing in again.

The top-right language selector supports Simplified Chinese, Traditional Chinese, and English. This guide follows the selected language. Use the book icon at the top right to return here at any time.

## Exception logs and troubleshooting

System administrators can filter system exceptions, device read failures, and browser exceptions in Exception logs, open sanitized details, and copy them for maintenance staff. Expected business validation failures are not recorded as system exceptions.

| Symptom | Suggested checks |
| --- | --- |
| No readings or gaps in the overview | Check whether the device is enabled, its latest sampling time, WebSocket status, and warnings; test the connection or sample manually when permitted |
| Connection failure | Check the BMC address, device credentials, network, certificate policy, and fingerprint; follow the displayed error |
| Console unavailable | Inspect firmware/capability notices, session status, and browser restrictions; avoid creating repeated connections with unknown results |
| Power actions disabled | Check power permission, the global switch, current capabilities, and pending reviews |
| Mail outcome unknown | Query the original request and sent-mail details, and verify the mail-server result before any repeat send |
| Assistance needed | Provide the time, server name, steps, error code, or sanitized exception summary; do not send device or file passwords |
