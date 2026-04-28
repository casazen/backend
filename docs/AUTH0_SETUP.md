# Auth0 Configuration Guide

> **Issue #46**: Auth0 Client Credentials Grant setup for M2M (machine-to-machine) authentication

## Overview

CasaZen uses Auth0 for authentication. This guide explains how to configure Auth0 for both:
1. **User Authentication** (JWT tokens for frontend)
2. **M2M Authentication** (Client Credentials for API testing and backend services)

---

## Prerequisites

- Auth0 account (free tier works for development)
- Access to Auth0 Dashboard: https://manage.auth0.com

---

## 1. Create Auth0 Application

### Step 1: Create Application

1. Go to Auth0 Dashboard → **Applications** → **Applications**
2. Click **Create Application**
3. Name: `CasaZen API`
4. Type: **Single Page Web Application** (for frontend)
5. Click **Create**

### Step 2: Configure Application Settings

In the application settings:

```
Allowed Callback URLs:
http://localhost:3000/callback
http://localhost:5173/callback
https://casazen.app/callback

Allowed Logout URLs:
http://localhost:3000
http://localhost:5173
https://casazen.app

Allowed Web Origins:
http://localhost:3000
http://localhost:5173
https://casazen.app
```

Save changes.

---

## 2. Create Auth0 API

### Step 1: Create API

1. Go to **Applications** → **APIs**
2. Click **Create API**
3. Configure:
   - **Name**: `CasaZen Backend API`
   - **Identifier**: `https://api.casazen.app` (this is your audience)
   - **Signing Algorithm**: `RS256`
4. Click **Create**

### Step 2: Configure API Permissions (Scopes)

In the API settings, go to **Permissions** tab and add:

| Scope | Description |
|-------|-------------|
| `read:properties` | Read property data |
| `write:properties` | Create/update properties |
| `read:bookings` | Read booking data |
| `write:bookings` | Create/update bookings |
| `read:payments` | Read payment data |
| `process:payments` | Process payments and refunds |

---

## 3. Configure M2M Authentication (Client Credentials)

> **This fixes Issue #46** - Enables API testing with JWT tokens

### Step 1: Create M2M Application

1. Go to **Applications** → **Applications**
2. Click **Create Application**
3. Name: `CasaZen API Testing`
4. Type: **Machine to Machine Applications**
5. Select API: `CasaZen Backend API`
6. Grant **all permissions** (for testing)
7. Click **Authorize**

### Step 2: Get Credentials

In the new M2M application settings, copy:
- **Client ID**: `YOUR_CLIENT_ID`
- **Client Secret**: `YOUR_CLIENT_SECRET`
- **Domain**: `dev-xxxxxxx.us.auth0.com`

### Step 3: Test M2M Authentication

```bash
# Get access token
curl --request POST \
  --url https://YOUR_DOMAIN/oauth/token \
  --header 'content-type: application/json' \
  --data '{
    "client_id":"YOUR_CLIENT_ID",
    "client_secret":"YOUR_CLIENT_SECRET",
    "audience":"https://api.casazen.app",
    "grant_type":"client_credentials"
  }'

# Response:
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "token_type": "Bearer",
  "expires_in": 86400
}

# Use token to call API
curl --request GET \
  --url https://localhost:5001/api/properties \
  --header 'Authorization: Bearer YOUR_ACCESS_TOKEN'
```

---

## 4. Configure Backend (appsettings.json)

Update `appsettings.Development.json`:

```json
{
  "Auth0": {
    "Domain": "dev-xxxxxxx.us.auth0.com",
    "Audience": "https://api.casazen.app",
    "ClientId": "YOUR_SPA_CLIENT_ID",
    "ClientSecret": "YOUR_SPA_CLIENT_SECRET"
  }
}
```

**Security**: Never commit `appsettings.Development.json` (already in `.gitignore`)

---

## 5. User Registration Flow

### Current Implementation

**POST /api/auth/register** creates user in local database only.

**Production Implementation** (TODO):

1. Create user in Auth0 via Management API
2. Get Auth0 `user_id` (sub claim)
3. Store user in local database with Auth0 `user_id`
4. Send verification email via Auth0

### Management API Setup

To enable user registration via Auth0:

1. Go to **Applications** → **APIs** → **Auth0 Management API**
2. Authorize your M2M application
3. Grant permissions:
   - `create:users`
   - `read:users`
   - `update:users`
   - `delete:users`

4. Update `UserService.RegisterUserAsync`:

```csharp
// Get Management API token
var managementToken = await GetManagementApiTokenAsync();

// Create user in Auth0
var auth0User = await _httpClient.PostAsJsonAsync(
    $"https://{_domain}/api/v2/users",
    new {
        email = email,
        password = password,
        name = $"{firstName} {lastName}",
        connection = "Username-Password-Authentication"
    },
    headers: new { Authorization = $"Bearer {managementToken}" }
);

// Use Auth0 user_id in local database
user.Id = auth0User.user_id;
```

---

## 6. Testing with Postman

### Get Token

```
POST https://YOUR_DOMAIN/oauth/token
Content-Type: application/json

{
  "client_id": "YOUR_CLIENT_ID",
  "client_secret": "YOUR_CLIENT_SECRET",
  "audience": "https://api.casazen.app",
  "grant_type": "client_credentials"
}
```

### Call Protected Endpoint

```
GET https://localhost:5001/api/properties
Authorization: Bearer YOUR_ACCESS_TOKEN
```

---

## 7. Common Issues

### Issue: "Unauthorized" even with valid token

**Solution**: Check `appsettings.json`:
- `Domain` matches Auth0 tenant
- `Audience` matches API identifier exactly

### Issue: "client_credentials not enabled"

**Solution**:
1. Go to Auth0 Dashboard → **Applications** → **APIs** → Your API
2. Go to **Machine to Machine Applications** tab
3. Toggle **Authorized** for your M2M application
4. Grant required permissions

### Issue: Token has no scopes

**Solution**: Grant permissions to M2M application in Auth0 Dashboard

---

## 8. Security Best Practices

1. ✅ **Use different Auth0 tenants for dev/staging/production**
2. ✅ **Rotate client secrets regularly**
3. ✅ **Use least-privilege principle for M2M apps**
4. ✅ **Enable MFA for Auth0 dashboard access**
5. ✅ **Monitor Auth0 logs for suspicious activity**
6. ❌ **Never commit Auth0 credentials to version control**
7. ❌ **Never expose client secrets in frontend code**

---

## Next Steps

- [ ] Configure Auth0 tenant (dev, staging, production)
- [ ] Create M2M application for API testing
- [ ] Test token generation and API calls
- [ ] Implement Auth0 Management API for user registration
- [ ] Set up Auth0 Actions/Rules for custom claims
- [ ] Configure email templates in Auth0

---

**Last Updated**: 2026-04-02
**Issue**: #46
**Status**: M2M setup documented, user registration TODO
