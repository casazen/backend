# Agency invitations

The platform super admin uses the staff console's **Inviti** entry (also available from **Utenti**) to invite the founder of a new agency. The staff flow does not select an existing organization or invite suppliers. After creation, the same personal link sent by email can be copied, downloaded as a QR or printed. It expires after seven days; resend rotates the token and revoke invalidates it.

The invitee must sign in with the invited, verified email. Acceptance precedes onboarding. Provisioning a new organization checks the accepted staff invitation and sets `Org.IsAgency`; ordinary signup leaves it false. Completing onboarding creates the `OrgRole.Owner` membership, with administration of that organization. It never assigns the platform `Admin` role or `platform_admin` membership. The platform role is displayed as **Super admin**, separately from organization administrators.

Agency identity is independent of billing tier. The new agency starts on Starter; accepting an invitation does not grant a paid subscription. Agencies can invite unlimited collaborators: neither the former daily invitation cap nor plan seat counts restrict them. Repeated delivery of the same invitation retains its existing resend interval. Private owners cannot create, resend or accept collaborator invitations; the API returns `403 org_agency_required`.

Apply migration `20261116230000_AddAgencyIdentity` with the backend release. Existing organizations on the Agency tier are backfilled as agencies. No live migration is applied by this change.

The legacy `/app/admin/invite-qr` page redirects to personal agency invitations. Generic QR codes that route to self-service signup cannot identify an agency founder.
