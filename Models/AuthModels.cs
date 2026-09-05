using System;

namespace ImajinationAPI.Models
{
    // Used for the Login endpoint
    public class LoginDto
    {
        public string email { get; set; } = string.Empty;
        public string password { get; set; } = string.Empty;
    }

    // Used to trigger the Email OTP sending
    public class SendOtpDto
    {
        public string email { get; set; } = string.Empty;
    }

    // Used for the final Registration insert
    public class RegisterDto
    {
        public string role { get; set; } = string.Empty;
        public string firstName { get; set; } = string.Empty;
        public string? middleName { get; set; }
        public string lastName { get; set; } = string.Empty;
        public string? suffix { get; set; }
        public string username { get; set; } = string.Empty;
        public string email { get; set; } = string.Empty;
        public string? contactNumber { get; set; }
        public string? address { get; set; }
        public DateTime? birthday { get; set; }
        public int age { get; set; }
        public string password { get; set; } = string.Empty;

        // Nullable fields for specific roles
        public string? stageName { get; set; }
        public string? productionName { get; set; }
        public string? talentCategory { get; set; }
        public string? memberNames { get; set; }

        // The 6-digit verification code
        public string? otp { get; set; }
        public bool termsAccepted { get; set; }
        public string? termsVersion { get; set; }
    }

    // Used for the Forgot Password flow
    public class ResetPasswordDto
    {
        public string email { get; set; } = string.Empty;
        public string otp { get; set; } = string.Empty;
        public string newPassword { get; set; } = string.Empty;
    }

    public class GoogleLoginDto
    {
        public string credential { get; set; } = string.Empty;
    }

    public class RefreshSessionDto
    {
        public Guid userId { get; set; }
        public string? refreshToken { get; set; }
    }
}
