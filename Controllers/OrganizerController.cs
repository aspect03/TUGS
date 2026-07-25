using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;
using System.Security.Claims;

namespace ImajinationAPI.Controllers
{
    public class UpdateOrganizerProfileDto
    {
        public string? firstName { get; set; }
        public string? lastName { get; set; }
        public string? productionName { get; set; }
        public string? contactNumber { get; set; }
        public string? address { get; set; }
        public string? bio { get; set; }
        public string? profilePicture { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class OrganizerController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly UploadScanningService _uploadScanningService;
        private readonly AutomatedVerificationAssessmentService _automatedVerificationAssessmentService;

        public OrganizerController(
            IConfiguration configuration,
            UploadScanningService uploadScanningService,
            AutomatedVerificationAssessmentService automatedVerificationAssessmentService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _uploadScanningService = uploadScanningService;
            _automatedVerificationAssessmentService = automatedVerificationAssessmentService;
        }

        private Guid? GetActorUserId() =>
            Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId) ? parsedUserId : null;

        private bool IsAdmin() =>
            string.Equals(User.FindFirstValue(ClaimTypes.Role), "Admin", StringComparison.OrdinalIgnoreCase);

        private bool CanAccessOwnOrganizerRecord(Guid targetUserId)
        {
            var actorUserId = GetActorUserId();
            return IsAdmin() || (actorUserId.HasValue && actorUserId.Value == targetUserId);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetOrganizerById(Guid id)
        {
            try
            {
                var canViewPrivateFields = CanAccessOwnOrganizerRecord(id);
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);

                const string sql = @"
                    SELECT firstname, lastname, productionname, profile_picture, email, username, contactnumber, address, bio, COALESCE(is_verified, FALSE)
                    FROM users
                    WHERE id = @id AND role = 'Organizer'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "Organizer not found." });
                }

                string first = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string last = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string productionName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                string profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
                string email = reader.IsDBNull(4) ? "" : reader.GetString(4);
                string username = reader.IsDBNull(5) ? "" : reader.GetString(5);
                string contactNumber = reader.IsDBNull(6) ? "" : reader.GetString(6);
                string address = reader.IsDBNull(7) ? "" : reader.GetString(7);
                string bio = reader.IsDBNull(8) ? "" : reader.GetString(8);
                bool isVerified = !reader.IsDBNull(9) && reader.GetBoolean(9);
                var profileSummary = CommunitySupport.CalculateProfileCompletion("Organizer", first, last, bio, profilePicture, "", "", productionName, contactNumber, address, "");
                await reader.CloseAsync();
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Organizer", first, last, bio, profilePicture, "", "", productionName, contactNumber, address, "");
                var verification = await CommunitySupport.GetTalentVerificationSnapshotAsync(connection, id, "Organizer");
                DateTime? verificationAssetSubmittedAt = null;
                bool hasVerificationIdFront = false;
                bool hasVerificationIdBack = false;
                bool hasVerificationSelfie = false;
                string verificationIdType = string.Empty;
                string verificationIdLast4 = string.Empty;
                string verificationEvidenceSummary = string.Empty;
                string verificationSupportingLinks = string.Empty;
                string verificationReferenceName = string.Empty;
                string verificationReferenceContact = string.Empty;

                const string verificationAssetsSql = @"
                    SELECT created_at,
                           COALESCE(id_type, ''),
                           COALESCE(id_number_last4, ''),
                           COALESCE(evidence_summary, ''),
                           COALESCE(supporting_links, ''),
                           COALESCE(reference_name, ''),
                           COALESCE(reference_contact, ''),
                           COALESCE(id_image_front, ''),
                           COALESCE(id_image_back, ''),
                           COALESCE(selfie_image, '')
                    FROM talent_verification_requests
                    WHERE user_id = @id
                      AND role = 'Organizer'
                    ORDER BY created_at DESC
                    LIMIT 1;";

                using (var verificationAssetsCmd = new NpgsqlCommand(verificationAssetsSql, connection))
                {
                    verificationAssetsCmd.Parameters.AddWithValue("@id", id);

                    using var verificationAssetsReader = await verificationAssetsCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                    if (await verificationAssetsReader.ReadAsync())
                    {
                        verificationAssetSubmittedAt = verificationAssetsReader.IsDBNull(0) ? null : (DateTime?)verificationAssetsReader.GetDateTime(0);
                        verificationIdType = verificationAssetsReader.IsDBNull(1) ? string.Empty : verificationAssetsReader.GetString(1);
                        verificationIdLast4 = verificationAssetsReader.IsDBNull(2) ? string.Empty : verificationAssetsReader.GetString(2);
                        verificationEvidenceSummary = verificationAssetsReader.IsDBNull(3) ? string.Empty : verificationAssetsReader.GetString(3);
                        verificationSupportingLinks = verificationAssetsReader.IsDBNull(4) ? string.Empty : verificationAssetsReader.GetString(4);
                        verificationReferenceName = verificationAssetsReader.IsDBNull(5) ? string.Empty : verificationAssetsReader.GetString(5);
                        verificationReferenceContact = verificationAssetsReader.IsDBNull(6) ? string.Empty : verificationAssetsReader.GetString(6);
                        hasVerificationIdFront = !verificationAssetsReader.IsDBNull(7) && !string.IsNullOrWhiteSpace(verificationAssetsReader.GetString(7));
                        hasVerificationIdBack = !verificationAssetsReader.IsDBNull(8) && !string.IsNullOrWhiteSpace(verificationAssetsReader.GetString(8));
                        hasVerificationSelfie = !verificationAssetsReader.IsDBNull(9) && !string.IsNullOrWhiteSpace(verificationAssetsReader.GetString(9));
                    }
                }

                decimal averageRating = 0;
                int reviewCount = 0;
                const string ratingSql = @"
                    SELECT COALESCE(AVG(er.rating), 0), COUNT(er.id)
                    FROM event_reviews er
                    INNER JOIN events e ON e.id = er.event_id
                    WHERE e.organizer_id = @id;";

                using (var ratingCmd = new NpgsqlCommand(ratingSql, connection))
                {
                    ratingCmd.Parameters.AddWithValue("@id", id);
                    using var ratingReader = await ratingCmd.ExecuteReaderAsync();
                    if (await ratingReader.ReadAsync())
                    {
                        averageRating = ratingReader.IsDBNull(0) ? 0 : ratingReader.GetDecimal(0);
                        reviewCount = ratingReader.IsDBNull(1) ? 0 : Convert.ToInt32(ratingReader.GetInt64(1));
                    }
                }

                var recentGigs = new List<object>();
                const string gigsSql = @"
                    SELECT id,
                           COALESCE(title, 'Untitled Event'),
                           event_time,
                           COALESCE(city, ''),
                           COALESCE(location, ''),
                           COALESCE(status, 'Upcoming'),
                           COALESCE(poster_url, '')
                    FROM events
                    WHERE organizer_id = @id
                    ORDER BY event_time DESC
                    LIMIT 6;";

                using (var gigsCmd = new NpgsqlCommand(gigsSql, connection))
                {
                    gigsCmd.Parameters.AddWithValue("@id", id);
                    using var gigsReader = await gigsCmd.ExecuteReaderAsync();
                    while (await gigsReader.ReadAsync())
                    {
                        recentGigs.Add(new
                        {
                            id = gigsReader.GetGuid(0),
                            title = gigsReader.IsDBNull(1) ? "Untitled Event" : gigsReader.GetString(1),
                            time = gigsReader.IsDBNull(2) ? DateTime.UtcNow : gigsReader.GetDateTime(2),
                            city = gigsReader.IsDBNull(3) ? "" : gigsReader.GetString(3),
                            location = gigsReader.IsDBNull(4) ? "" : gigsReader.GetString(4),
                            status = gigsReader.IsDBNull(5) ? "Upcoming" : gigsReader.GetString(5),
                            posterUrl = gigsReader.IsDBNull(6) ? "" : gigsReader.GetString(6)
                        });
                    }
                }

                return Ok(new
                {
                    displayName = !string.IsNullOrWhiteSpace(productionName)
                        ? productionName
                        : $"{first} {last}".Trim(),
                    firstName = first,
                    lastName = last,
                    productionName,
                    profilePicture,
                    email = canViewPrivateFields ? email : "",
                    username,
                    contactNumber = canViewPrivateFields ? contactNumber : "",
                    address = canViewPrivateFields ? address : "",
                    bio,
                    isVerified = verification.HasApprovedRequest,
                    profileCompletionPercent = profileSummary.Percent,
                    profileCompletionLabel = profileSummary.Label,
                    verificationStatus = verification.Status,
                    verificationLevel = verification.Level,
                    verificationMethod = verification.Method,
                    verificationNotes = canViewPrivateFields ? verification.Notes : "",
                    verificationSubmittedAt = canViewPrivateFields ? verification.SubmittedAt : null,
                    verificationReviewedAt = canViewPrivateFields ? verification.ReviewedAt : null,
                    verificationUploads = canViewPrivateFields
                        ? new
                        {
                            idFrontSubmitted = hasVerificationIdFront,
                            idBackSubmitted = hasVerificationIdBack,
                            selfieSubmitted = hasVerificationSelfie,
                            submittedAt = verificationAssetSubmittedAt
                        }
                        : null,
                    verificationRequest = canViewPrivateFields
                        ? new
                        {
                            idType = verificationIdType,
                            idLast4 = verificationIdLast4,
                            evidenceSummary = verificationEvidenceSummary,
                            supportingLinks = verificationSupportingLinks,
                            referenceName = verificationReferenceName,
                            referenceContact = verificationReferenceContact
                        }
                        : null,
                    averageRating = Math.Round(averageRating, 1),
                    reviewCount,
                    recentGigs
                });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to load organizer profile." });
            }
        }

        [Authorize(Roles = "Organizer")]
        [HttpPut("{id}/profile")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> UpdateOrganizerProfile(Guid id, [FromBody] UpdateOrganizerProfileDto req)
        {
            try
            {
                if (!CanAccessOwnOrganizerRecord(id))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedFirstName = SecuritySupport.SanitizePlainText(req.firstName, 120, false);
                var sanitizedLastName = SecuritySupport.SanitizePlainText(req.lastName, 120, false);
                var sanitizedProductionName = SecuritySupport.SanitizePlainText(req.productionName, 160, false);
                var sanitizedContactNumber = SecuritySupport.SanitizePlainText(req.contactNumber, 60, false);
                var sanitizedAddress = SecuritySupport.SanitizePlainText(req.address, 240, true);
                var sanitizedBio = SecuritySupport.SanitizePlainText(req.bio, 2500, true);
                var normalizedPicture = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.profilePicture, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }
                var pictureScan = await _uploadScanningService.ScanDataUrlAsync(normalizedPicture, "organizer profile image");
                if (!pictureScan.IsClean)
                {
                    return BadRequest(new { message = pictureScan.Message });
                }

                const string sql = @"
                    UPDATE users
                    SET firstname = COALESCE(@firstName, firstname),
                        lastname = COALESCE(@lastName, lastname),
                        productionname = COALESCE(@productionName, productionname),
                        contactnumber = COALESCE(@contactNumber, contactnumber),
                        address = COALESCE(@address, address),
                        bio = COALESCE(@bio, bio),
                        profile_picture = COALESCE(@profilePicture, profile_picture)
                    WHERE id = @id AND role = 'Organizer'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                cmd.Parameters.Add("@firstName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedFirstName) ? DBNull.Value : sanitizedFirstName;
                cmd.Parameters.Add("@lastName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedLastName) ? DBNull.Value : sanitizedLastName;
                cmd.Parameters.Add("@productionName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedProductionName) ? DBNull.Value : sanitizedProductionName;
                cmd.Parameters.Add("@contactNumber", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedContactNumber) ? DBNull.Value : sanitizedContactNumber;
                cmd.Parameters.Add("@address", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedAddress) ? DBNull.Value : sanitizedAddress;
                cmd.Parameters.Add("@bio", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedBio) ? DBNull.Value : sanitizedBio;
                cmd.Parameters.Add("@profilePicture", NpgsqlDbType.Text).Value = (object?)normalizedPicture ?? DBNull.Value;

                var updated = await cmd.ExecuteNonQueryAsync();
                if (updated == 0)
                {
                    return BadRequest(new { message = "Update failed. Make sure you are an Organizer." });
                }
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Organizer", sanitizedFirstName, sanitizedLastName, sanitizedBio, normalizedPicture, "", "", sanitizedProductionName, sanitizedContactNumber, sanitizedAddress, "");
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    id,
                    "Organizer",
                    "profile_updated",
                    "user",
                    id,
                    HttpContext,
                    "Organizer profile updated.");

                return Ok(new { message = "Organizer profile updated successfully." });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Unable to update organizer profile right now." });
            }
        }

        [Authorize(Roles = "Organizer")]
        [HttpPost("{id}/verification-request")]
        public async Task<IActionResult> SubmitVerificationRequest(Guid id, [FromBody] SubmitTalentVerificationRequestDto req)
        {
            try
            {
                if (!CanAccessOwnOrganizerRecord(id))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                if (!req.consentConfirmed || !req.faceVerificationConsent)
                {
                    return BadRequest(new { message = "You must consent to ID and facial verification before submitting." });
                }

                var verificationPath = SecuritySupport.SanitizePlainText(req.verificationPath, 60, true) ?? "PhilippineIdAndFace";
                var evidenceSummary = SecuritySupport.SanitizePlainText(req.evidenceSummary, 1200, true);
                var idType = SecuritySupport.SanitizePlainText(req.idType, 60, false);
                var idNumberLast4 = SecuritySupport.SanitizePlainText(req.idNumberLast4, 8, false);
                if (string.IsNullOrWhiteSpace(evidenceSummary))
                {
                    return BadRequest(new { message = "Add a short summary for the verification reviewer." });
                }
                if (string.IsNullOrWhiteSpace(idType))
                {
                    return BadRequest(new { message = "Choose the Philippine ID type you are submitting." });
                }
                if (string.IsNullOrWhiteSpace(idNumberLast4) || idNumberLast4.Length < 4)
                {
                    return BadRequest(new { message = "Enter the last 4 characters of the submitted ID number." });
                }

                var normalizedIdFront = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.idImageFront, 3_500_000, out var idFrontError);
                if (idFrontError is not null)
                {
                    return BadRequest(new { message = idFrontError });
                }
                if (string.IsNullOrWhiteSpace(normalizedIdFront))
                {
                    return BadRequest(new { message = "Upload the front image of your Philippine ID." });
                }

                var normalizedIdBack = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.idImageBack, 3_500_000, out var idBackError);
                if (idBackError is not null)
                {
                    return BadRequest(new { message = idBackError });
                }

                var normalizedSelfie = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.selfieImage, 3_500_000, out var selfieError);
                if (selfieError is not null)
                {
                    return BadRequest(new { message = selfieError });
                }
                if (string.IsNullOrWhiteSpace(normalizedSelfie))
                {
                    return BadRequest(new { message = "Upload a clear selfie for facial verification." });
                }

                var idFrontScan = await _uploadScanningService.ScanDataUrlAsync(normalizedIdFront, "organizer ID front");
                if (!idFrontScan.IsClean)
                {
                    return BadRequest(new { message = idFrontScan.Message });
                }
                var idBackScan = await _uploadScanningService.ScanDataUrlAsync(normalizedIdBack, "organizer ID back");
                if (!idBackScan.IsClean)
                {
                    return BadRequest(new { message = idBackScan.Message });
                }
                var selfieScan = await _uploadScanningService.ScanDataUrlAsync(normalizedSelfie, "organizer verification selfie");
                if (!selfieScan.IsClean)
                {
                    return BadRequest(new { message = selfieScan.Message });
                }

                var automatedAssessment = _automatedVerificationAssessmentService.Assess(
                    idType,
                    idNumberLast4,
                    evidenceSummary,
                    normalizedIdFront,
                    normalizedIdBack,
                    normalizedSelfie);

                const string pendingSql = @"
                    SELECT COUNT(*)
                    FROM talent_verification_requests
                    WHERE user_id = @userId
                      AND status = 'Pending';";
                await using (var pendingCmd = new NpgsqlCommand(pendingSql, connection))
                {
                    pendingCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = id;
                    var pendingCount = Convert.ToInt32(await pendingCmd.ExecuteScalarAsync() ?? 0);
                    if (pendingCount > 0)
                    {
                        return Conflict(new { message = "A verification request is already under review." });
                    }
                }

                const string insertSql = @"
                    INSERT INTO talent_verification_requests (
                        id, user_id, role, verification_path, evidence_summary, portfolio_links, supporting_links, reference_name, reference_contact,
                        id_type, id_number_last4, id_image_front, id_image_back, selfie_image, consent_confirmed, face_verification_consent,
                        id_review_status, facial_review_status, automated_status, automated_recommendation, automated_score, automated_notes, automated_reviewed_at, status, created_at
                    )
                    VALUES (
                        @id, @userId, 'Organizer', @verificationPath, @evidenceSummary, @portfolioLinks, @supportingLinks, @referenceName, @referenceContact,
                        @idType, @idNumberLast4, @idImageFront, @idImageBack, @selfieImage, @consentConfirmed, @faceVerificationConsent,
                        'Pending', 'Pending', @automatedStatus, @automatedRecommendation, @automatedScore, @automatedNotes, NOW(), 'Pending', NOW()
                    );";
                await using (var insertCmd = new NpgsqlCommand(insertSql, connection))
                {
                    insertCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                    insertCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = id;
                    insertCmd.Parameters.Add("@verificationPath", NpgsqlDbType.Text).Value = verificationPath;
                    insertCmd.Parameters.Add("@evidenceSummary", NpgsqlDbType.Text).Value = evidenceSummary;
                    insertCmd.Parameters.Add("@portfolioLinks", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.portfolioLinks, 1500, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@supportingLinks", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.supportingLinks, 1500, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@referenceName", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.referenceName, 160, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@referenceContact", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.referenceContact, 160, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@idType", NpgsqlDbType.Text).Value = idType;
                    insertCmd.Parameters.Add("@idNumberLast4", NpgsqlDbType.Text).Value = idNumberLast4;
                    insertCmd.Parameters.Add("@idImageFront", NpgsqlDbType.Text).Value = SecuritySupport.ProtectSensitiveData(normalizedIdFront, _connectionString);
                    insertCmd.Parameters.Add("@idImageBack", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(normalizedIdBack)
                        ? DBNull.Value
                        : (object)SecuritySupport.ProtectSensitiveData(normalizedIdBack, _connectionString);
                    insertCmd.Parameters.Add("@selfieImage", NpgsqlDbType.Text).Value = SecuritySupport.ProtectSensitiveData(normalizedSelfie, _connectionString);
                    insertCmd.Parameters.Add("@consentConfirmed", NpgsqlDbType.Boolean).Value = req.consentConfirmed;
                    insertCmd.Parameters.Add("@faceVerificationConsent", NpgsqlDbType.Boolean).Value = req.faceVerificationConsent;
                    insertCmd.Parameters.Add("@automatedStatus", NpgsqlDbType.Text).Value = automatedAssessment.Status;
                    insertCmd.Parameters.Add("@automatedRecommendation", NpgsqlDbType.Text).Value = automatedAssessment.Recommendation;
                    insertCmd.Parameters.Add("@automatedScore", NpgsqlDbType.Integer).Value = automatedAssessment.Score;
                    insertCmd.Parameters.Add("@automatedNotes", NpgsqlDbType.Text).Value = automatedAssessment.Notes;
                    await insertCmd.ExecuteNonQueryAsync();
                }

                const string updateSql = @"
                    UPDATE users
                    SET verification_status = 'Pending',
                        verification_level = 'Identity Review',
                        verification_method = @verificationMethod,
                        verification_notes = 'Submitted Philippine ID and selfie evidence. Waiting for admin review.',
                        verification_last_submitted_at = NOW()
                    WHERE id = @userId;";
                await using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = id;
                updateCmd.Parameters.Add("@verificationMethod", NpgsqlDbType.Text).Value = "Philippine ID + Facial Review";
                await updateCmd.ExecuteNonQueryAsync();

                return Ok(new
                {
                    message = "Organizer identity verification submitted. Automated screening is complete and the request is now waiting for admin review.",
                    status = "Pending",
                    automatedStatus = automatedAssessment.Status,
                    automatedRecommendation = automatedAssessment.Recommendation,
                    automatedScore = automatedAssessment.Score
                });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to submit organizer verification request." });
            }
        }
    }
}
