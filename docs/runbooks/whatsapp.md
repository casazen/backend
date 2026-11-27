# WhatsApp Business

CasaZen sends rent reminders through Meta WhatsApp Cloud API. It never falls back to a fake success: a reminder is recorded only when the API returns a message id.

## Configuration

Supply these values as environment variables; never commit the access token:

- `WhatsApp__Enabled=true`
- `WhatsApp__PhoneNumberId=<Meta phone number id>`
- `WhatsApp__AccessToken=<system-user permanent token>`
- `WhatsApp__ApiVersion=v22.0` (override when upgrading the Graph API)
- `WhatsApp__RentReminderTemplate=rent_reminder`
- `WhatsApp__LanguageCode=it`

The Meta Business account must own and verify the sending number. Create and obtain approval for a Utility template named `rent_reminder` with five body variables, in this order:

1. tenant name;
2. amount;
3. property name;
4. due date;
5. landlord note (`-` when absent).

The recipient phone is stored in E.164 form and encrypted at rest. API errors and logs never include phone numbers, template values or tokens.

For production, use a long-lived system-user token with the minimum WhatsApp messaging permissions and rotate it through the secret store. Keep `Enabled=false` until the number and template are approved.
