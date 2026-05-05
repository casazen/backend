# Auth0 Authentication Setup Guide

> **Issue #98** | Last Updated: 2026-05-05

Complete guide to configure Auth0 for CasaZen. A new developer should be up and running in under 30 minutes.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Create Auth0 Application (Frontend SPA)](#2-create-auth0-application-frontend-spa)
3. [Create Auth0 API (Backend)](#3-create-auth0-api-backend)
4. [Configure Auth0 Roles & Custom Claims](#4-configure-auth0-roles--custom-claims)
5. [Backend Configuration](#5-backend-configuration)
6. [Frontend Configuration](#6-frontend-configuration)
7. [Testing with Postman / M2M Token](#7-testing-with-postman--m2m-token)
8. [Local Development Workflow](#8-local-development-workflow)
9. [Troubleshooting](#9-troubleshooting)
10. [Security Best Practices](#10-security-best-practices)

---

## 1. Prerequisites

- Auth0 account (free tier works for development): https://auth0.com
- Access to Auth0 Dashboard: https://manage.auth0.com
- Backend running locally at `https://localhost:5001`
- Frontend running locally at `http://localhost:5173`

---

## 2. Create Auth0 Application (Frontend SPA)

### Step 1: Create Application

1. Auth0 Dashboard → **Applications** → **Applications** → **Create Application**
2. Name: `CasaZen Frontend`
3. Type: **Single Page Web Applications**
4. Click **Create**

### Step 2: Configure Application Settings

In the application settings tab, set:

```
Allowed Callback URLs:
  http://localhost:5173/callback
  https://casazen.app/callback

Allowed Logout URLs:
  http://localhost:5173
  https://casazen.app

Allowed Web Origins:
  http://localhost:5173
  https://casazen.app
```

Save changes.

### Step 3: Note Your Credentials

Copy from the **Basic Information** section:
- **Domain**: `dev-xxxxxxxx.us.auth0.com`
- **Client ID**: `abc123...` (used in frontend config)

> **Important**: Do not copy the Client Secret for SPA apps — SPAs use PKCE, not client secret.

---

## 3. Create Auth0 API (Backend)

### Step 1: Create API

1. Auth0 Dashboard → **Applications** → **APIs** → **Create API**
2. Configure:
   - **Name**: `CasaZen Backend API`
   - **Identifier (Audience)**: `https://api.casazen.app`
   - **Signing Algorithm**: `RS256`
3. Click **Create**

### Step 2: Enable RBAC

In the API settings → **RBAC Settings**:
- Enable **Enable RBAC**
- Enable **Add Permissions in the Access Token**

### Step 3: Add Permissions (Scopes)

In the API settings → **Permissions** tab:

| Scope | Description |
|-------|-------------|
| `read:properties` | Read property data |
| `write:properties` | Create/update properties |
| `delete:properties` | Delete properties |
| `read:bookings` | Read booking data |
| `write:bookings` | Create/update bookings |
| `read:payments` | Read payment data |
| `process:payments` | Process payments and refunds |
| `read:guests` | Read guest data |
| `write:guests` | Create/update guest data |
| `admin:all` | Full admin access |

---

## 4. Configure Auth0 Roles & Custom Claims

CasaZen maps Auth0 roles to .NET role claims via a custom claim namespace.

### Step 1: Create Roles

Auth0 Dashboard → **User Management** → **Roles** → **Create Role**:

| Role Name | Description |
|-----------|-------------|
| `Admin` | Full system access |
| `PropertyOwner` | Can manage own properties and bookings |

### Step 2: Create Auth0 Action for Custom Claims

Auth0 Dashboard → **Actions** → **Library** → **Create Action** → **Build from scratch**

- Name: `Add Roles to Token`
- Trigger: **Login / Post Login**

Paste this code:

```javascript
exports.onExecutePostLogin = async (event, api) => {
  const namespace = 'https://casazen.app';
  const roles = event.authorization?.roles ?? [];

  // Add roles as custom claim (required by backend)
  api.accessToken.setCustomClaim(`${namespace}/roles`, roles);

  // Add user metadata if needed
  if (event.user.user_metadata?.property_owner_id) {
    api.accessToken.setCustomClaim(`${namespace}/property_owner_id`,
      event.user.user_metadata.property_owner_id);
  }
};
```

Click **Deploy**.

### Step 3: Attach Action to Flow

Auth0 Dashboard → **Actions** → **Flows** → **Login** → drag the action into the flow → **Apply**.

### Step 4: Assign Roles to Users

Auth0 Dashboard → **User Management** → **Users** → select user → **Roles** tab → **Assign Roles**.

---

## 5. Backend Configuration

### Step 1: Set Environment Variables

Create or update `appsettings.Development.json` (already in `.gitignore`):

```json
{
  "Auth0": {
    "Domain": "dev-xxxxxxxx.us.auth0.com",
    "Audience": "https://api.casazen.app"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=CasazenDev;Trusted_Connection=True;"
  }
}
```

> **Never commit** `appsettings.Development.json` — it is gitignored.

### Step 2: How the Backend Uses Auth0

The backend (`ServiceCollectionExtensions.cs`) validates JWT tokens:

```csharp
options.Authority = $"https://{domain}";   // OIDC discovery endpoint
options.Audience = audience;               // Must match API Identifier

options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuer = true,
    ValidIssuer = $"https://{domain}/",    // Note trailing slash
    ValidateAudience = true,
    ValidAudience = audience,
    ValidateLifetime = true,
    ValidateIssuerSigningKey = true,
    NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
};
```

The `sub` claim from Auth0 (`auth0|abc123`) is used as the user identifier (mapped via the nameidentifier claim type).

**Role mapping**: The `OnTokenValidated` event reads the `https://casazen.app/roles` custom claim and maps roles to standard .NET role claims.

### Step 3: User ID Claim Resolution

> **Fixed in PR #111**: The backend searches for user ID across multiple claim types in this order:
> 1. `sub`
> 2. `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`
> 3. `nameidentifier`

This handles both Auth0 user tokens and M2M tokens transparently.

---

## 6. Frontend Configuration

### Step 1: Set Environment Variables

Create `frontend/.env.local`:

```env
VITE_AUTH0_DOMAIN=dev-xxxxxxxx.us.auth0.com
VITE_AUTH0_CLIENT_ID=abc123...
VITE_AUTH0_AUDIENCE=https://api.casazen.app
VITE_AUTH0_REDIRECT_URI=http://localhost:5173/callback
VITE_API_BASE_URL=http://localhost:5000
```

> **Never commit** `.env.local` — it is gitignored.

### Step 2: Auth0 SDK Setup

The frontend uses `@auth0/auth0-react`. The `Auth0Provider` is configured in `main.tsx`:

```tsx
<Auth0Provider
  domain={import.meta.env.VITE_AUTH0_DOMAIN}
  clientId={import.meta.env.VITE_AUTH0_CLIENT_ID}
  authorizationParams={{
    redirect_uri: import.meta.env.VITE_AUTH0_REDIRECT_URI,
    audience: import.meta.env.VITE_AUTH0_AUDIENCE,
  }}
>
  <App />
</Auth0Provider>
```

### Step 3: Token Injection

The axios instance (`src/lib/axios.ts`) automatically injects the Bearer token via a request interceptor:

```typescript
axios.interceptors.request.use(async (config) => {
  const token = await getAccessTokenSilently({
    authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE }
  });
  config.headers.Authorization = `Bearer ${token}`;
  return config;
});
```

Token refresh is handled automatically by `getAccessTokenSilently` (uses refresh tokens or silent iframe depending on Auth0 settings).

### Step 4: Login / Logout Flow

```typescript
const { loginWithRedirect, logout, isAuthenticated, user } = useAuth0();

// Login
await loginWithRedirect();

// Logout
logout({ logoutParams: { returnTo: window.location.origin } });
```

---

## 7. Testing with Postman / M2M Token

For API testing without the frontend (Postman, curl, integration tests).

### Step 1: Create M2M Application

1. Auth0 Dashboard → **Applications** → **Create Application**
2. Name: `CasaZen API Testing`
3. Type: **Machine to Machine Applications**
4. Select API: `CasaZen Backend API` → Grant all permissions
5. Click **Authorize**

### Step 2: Get Access Token

```bash
curl --request POST \
  --url "https://YOUR_DOMAIN/oauth/token" \
  --header "Content-Type: application/json" \
  --data '{
    "client_id": "YOUR_M2M_CLIENT_ID",
    "client_secret": "YOUR_M2M_CLIENT_SECRET",
    "audience": "https://api.casazen.app",
    "grant_type": "client_credentials"
  }'
```

Response:
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "token_type": "Bearer",
  "expires_in": 86400
}
```

### Step 3: Call Protected Endpoints

```bash
curl --request GET \
  --url "http://localhost:5000/api/properties" \
  --header "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

### Step 4: Postman Setup

1. In Postman collection → **Authorization** tab
2. Type: **OAuth 2.0**
3. Configure:
   - Grant type: **Client Credentials**
   - Access Token URL: `https://YOUR_DOMAIN/oauth/token`
   - Client ID: `YOUR_M2M_CLIENT_ID`
   - Client Secret: `YOUR_M2M_CLIENT_SECRET`
   - Scope: _(leave empty)_
   - Client Authentication: **Send as Basic Auth header**
4. Add `audience` to **Advanced** → **Body params**: `audience = https://api.casazen.app`

---

## 8. Local Development Workflow

### Start Backend

```bash
cd backend
dotnet run --project Casazen.Web
# API: http://localhost:5000
# Swagger: http://localhost:5000/swagger
```

### Start Frontend

```bash
cd frontend
npm run dev
# App: http://localhost:5173
```

### Verify Auth Flow

1. Navigate to `http://localhost:5173`
2. Click **Login** → Auth0 universal login page
3. Create account or use existing credentials
4. After redirect, check browser console — no 401 errors
5. Navigate to **Properties** page — should load real data

### Debug Token Issues

If you get 401 errors:

1. Open browser DevTools → **Application** → **Local Storage** or check auth state
2. Check backend logs for `Authentication failed:` messages
3. Decode token at https://jwt.io — verify:
   - `iss`: `https://YOUR_DOMAIN/` (with trailing slash)
   - `aud`: `https://api.casazen.app`
   - `exp`: not expired

---

## 9. Troubleshooting

### "Unauthorized" with valid token

**Cause**: Audience or issuer mismatch.

**Fix**:
- Backend `appsettings.json`: `Auth0:Audience` must exactly match the API Identifier in Auth0 dashboard
- Backend `Auth0:Domain` must NOT have `https://` prefix (just `dev-xxx.us.auth0.com`)
- Issuer validation expects trailing slash: `https://dev-xxx.us.auth0.com/`

### "Query data cannot be undefined" in frontend

**Cause**: API response format mismatch (already fixed in PR #50/#51).

**Fix**: Ensure frontend is on the latest `main` branch.

### "client_credentials not enabled"

**Cause**: M2M application not authorized for the API.

**Fix**:
1. Auth0 Dashboard → **Applications** → **APIs** → `CasaZen Backend API`
2. **Machine to Machine Applications** tab
3. Toggle **Authorized** for your M2M app
4. Grant required permissions

### User ID not found in claims

**Cause**: M2M tokens use `sub` claim in a different format than user tokens.

**Fix**: Already handled by the multi-claim-type lookup introduced in PR #111. No action needed.

### Roles not appearing in token

**Cause**: Auth0 Action not deployed or not added to the Login flow.

**Fix**:
1. Auth0 Dashboard → **Actions** → **Library** → verify `Add Roles to Token` action is **Deployed**
2. Auth0 Dashboard → **Actions** → **Flows** → **Login** → verify the action is in the flow

### 403 Forbidden after login

**Cause**: User has no roles assigned.

**Fix**: Auth0 Dashboard → **User Management** → **Users** → select user → **Roles** → **Assign Roles** → assign `PropertyOwner` or `Admin`.

---

## 10. Security Best Practices

| Practice | Status |
|----------|--------|
| Use RS256 signing algorithm | ✅ Configured |
| Validate issuer and audience | ✅ Configured |
| Token lifetime set to 24h max | ✅ Auth0 default |
| Client secrets never in frontend code | ✅ SPA uses PKCE |
| Different tenants for dev/staging/prod | ⚠️ Recommended |
| Rotate M2M client secrets regularly | ⚠️ Recommended |
| Enable MFA on Auth0 dashboard account | ⚠️ Recommended |
| Never commit credentials to version control | ✅ .gitignored |
| Use least-privilege for M2M apps | ✅ Grant only needed scopes |
| Monitor Auth0 logs for anomalies | ⚠️ Recommended in production |

---

## Related Files

| File | Purpose |
|------|---------|
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | JWT Bearer configuration |
| `Casazen.Web/Program.cs` | Middleware pipeline |
| `appsettings.json` | Auth0 config keys (no values) |
| `appsettings.Development.json` | Auth0 config values (gitignored) |
| `frontend/src/lib/axios.ts` | Token injection interceptor |
| `frontend/src/hooks/use-auth.ts` | Auth0 React hook wrapper |
| `frontend/src/components/auth/auth-initializer.tsx` | Auth0 initialization |

---

**Last Updated**: 2026-05-05
**Issue**: #98
**Related PRs**: #109, #111 (backend auth fixes), #51 (frontend auth fixes)
