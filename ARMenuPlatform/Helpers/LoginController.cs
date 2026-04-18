using ARMenuPlatform.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ARMenuPlatform.Controllers
{
    public class LoginController : Controller
    {
        private readonly DbHelper _db;

        public LoginController(DbHelper db)
        {
            _db = db;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /Login
        // Shows the login form.
        // If already logged in, skip the form and redirect to the right dashboard.
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet]
        [Route("Login")]
        public IActionResult Index()
        {
            // Already authenticated — bounce to the correct dashboard
            if (!string.IsNullOrEmpty(HttpContext.Session.GetString(SessionKeys.UserID)))
                return RedirectToDashboard(HttpContext.Session.GetString(SessionKeys.Role));

            return View();
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /Login
        // Validates credentials, builds session, redirects to dashboard.
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost]
        [Route("Login")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(string email, string password)
        {
            // ── Basic client-side style validation ──────────────────────────
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                ViewBag.Error = "Email and password are required.";
                return View();
            }

            // ── Hash the submitted password before hitting the database ──────
            // We NEVER send plaintext passwords to the DB query.
            string passwordHash = PasswordHelper.HashPassword(password.Trim());

            // ── Query the database ───────────────────────────────────────────
            // Pull the user row matching email + hash + active flag in one query.
            // We do NOT query by email alone and compare hashes in C# — that
            // would pull the hash over the wire unnecessarily.
            const string sql = @"
                SELECT
                    UserID,
                    FullName,
                    Email,
                    Role,
                    RestaurantID,
                    IsActive
                FROM Users
                WHERE Email        = @Email
                  AND PasswordHash = @PasswordHash";

            var dt = await _db.ExecuteQueryAsync(sql,
                new SqlParameter("@Email", email.Trim().ToLower()),
                new SqlParameter("@PasswordHash", passwordHash));

            // ── No matching row ──────────────────────────────────────────────
            if (dt.Rows.Count == 0)
            {
                // Deliberately vague — don't reveal whether email or password is wrong
                ViewBag.Error = "Invalid email or password.";
                return View();
            }

            var row = dt.Rows[0];

            // ── Account deactivated ──────────────────────────────────────────
            if (!Convert.ToBoolean(row["IsActive"]))
            {
                ViewBag.Error = "Your account has been deactivated. Please contact support.";
                return View();
            }

            // ── Stamp LastLoginDate ──────────────────────────────────────────
            // Fire-and-forget is acceptable here — login still succeeds even if
            // this update fails (non-critical timestamp).
            _ = _db.ExecuteNonQueryAsync(
                "UPDATE Users SET LastLoginDate = SYSUTCDATETIME() WHERE UserID = @UserID",
                new SqlParameter("@UserID", Convert.ToInt32(row["UserID"])));

            // ── Build session ────────────────────────────────────────────────
            // Store only IDs and scalars in session — never store sensitive data.
            HttpContext.Session.SetString(SessionKeys.UserID, row["UserID"].ToString()!);
            HttpContext.Session.SetString(SessionKeys.Role, row["Role"].ToString()!);
            HttpContext.Session.SetString(SessionKeys.FullName, row["FullName"].ToString()!);

            // RestaurantID is NULL for SuperAdmin — store empty string in that case.
            HttpContext.Session.SetString(
                SessionKeys.RestaurantID,
                row["RestaurantID"] == DBNull.Value ? "" : row["RestaurantID"].ToString()!);

            // ── Redirect to the right dashboard ─────────────────────────────
            return RedirectToDashboard(row["Role"].ToString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /Logout
        // Clears the entire session and redirects to Login.
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet]
        [Route("Logout")]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Login");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helper — centralise the role → dashboard routing in one place
        // ─────────────────────────────────────────────────────────────────────
        private IActionResult RedirectToDashboard(string? role)
        {
            return role switch
            {
                "SuperAdmin" => RedirectToAction("Dashboard", "Admin"),
                "RestaurantOwner" => RedirectToAction("Dashboard", "Restaurant"),
                _ => RedirectToAction("Index", "Login")  // unknown role — back to login
            };
        }
    }
}
