## Supabase Security And Recovery Setup

Use environment variables for local development and deployment so your database and Brevo credentials are not stored in `appsettings.json`.

### 1. Set your local environment variables in PowerShell

```powershell
$env:ConnectionStrings__SupabaseConnection = "Host=YOUR_SUPABASE_HOST;Port=5432;Database=postgres;Username=postgres;Password=YOUR_DB_PASSWORD;SSL Mode=Require;Trust Server Certificate=true;Timeout=5;Command Timeout=10;"
$env:EmailSettings__SmtpServer = "smtp-relay.brevo.com"
$env:EmailSettings__Port = "587"
$env:EmailSettings__SenderName = "Tugs! Accounts"
$env:EmailSettings__SenderEmail = "YOUR_VERIFIED_SENDER_EMAIL"
$env:EmailSettings__Username = "YOUR_BREVO_SMTP_USERNAME"
$env:EmailSettings__Password = "YOUR_BREVO_SMTP_PASSWORD"
$env:AppSecurity__AllowedOrigins__0 = "http://localhost:5248"
$env:AppSecurity__AllowedOrigins__1 = "https://localhost:5248"
$env:AppSecurity__AllowedOrigins__2 = "https://imajination.infinityfreeapp.com"
```

For your current live frontend, keep `https://imajination.infinityfreeapp.com` in the allowed origins list for deployment too.

### 2. Restart the app

The ASP.NET app reads those values on startup. Restart it after setting them.

### 3. Supabase backup strategy

Use all three together:

1. Supabase automated backups
   Turn on backups in your Supabase project settings if your plan supports it.

2. Point-in-time recovery
   Enable PITR if available on your plan so you can restore to a specific timestamp after accidental deletes or bad updates.

3. Manual export backups
   Keep your own scheduled logical backups outside Supabase.

### 4. Manual backup command

Use your Supabase Postgres connection string with `pg_dump`:

```powershell
pg_dump "Host=YOUR_SUPABASE_HOST;Port=5432;Database=postgres;Username=postgres;Password=YOUR_DB_PASSWORD;SSL Mode=Require;Trust Server Certificate=true" -Fc -f ".\backups\imajination_$(Get-Date -Format yyyyMMdd_HHmmss).dump"
```

This creates a compressed Postgres backup file.

### 5. Restore command

Restore into a fresh database or a restored Supabase project using:

```powershell
pg_restore --clean --if-exists --no-owner --dbname "Host=YOUR_SUPABASE_HOST;Port=5432;Database=postgres;Username=postgres;Password=YOUR_DB_PASSWORD;SSL Mode=Require;Trust Server Certificate=true" ".\backups\YOUR_BACKUP_FILE.dump"
```

### 6. Minimum recovery checklist

Keep these outside the app repo:

- Supabase project reference and region
- database password
- Brevo SMTP credentials
- sender verification status
- latest manual backup file
- restore test notes

### 7. Recommended routine

- Daily: automated Supabase backup
- Weekly: manual `pg_dump`
- Monthly: test a restore into a non-production database
- After schema changes: create an extra manual backup

### 8. Important note about OTP

If OTP still fails after the code changes, check Brevo:

- sender email is verified
- domain authentication is complete if using a branded domain
- transactional logs show Delivered instead of Rejected
