using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ARMenuPlatform.Helpers
{
    // =========================================================================
    // SESSION KEY CONSTANTS
    // Centralise all session key strings here so a typo in any controller
    // causes a compile error (via this class) rather than a silent auth bug.
    // =========================================================================
    public static class SessionKeys
    {
        public const string UserID = "UserID";
        public const string Role = "Role";
        public const string FullName = "FullName";
        public const string RestaurantID = "RestaurantID";
    }

    // =========================================================================
    // AdminAuthFilter
    // Allows any logged-in user (SuperAdmin OR RestaurantOwner).
    // Apply to a controller or action with: [TypeFilter(typeof(AdminAuthFilter))]
    // =========================================================================

    /// <summary>
    /// Blocks unauthenticated requests and redirects them to /Login.
    /// Attach to any controller or action that requires a logged-in user.
    ///
    /// Usage:
    ///   [TypeFilter(typeof(AdminAuthFilter))]
    ///   public class RestaurantController : Controller { ... }
    /// </summary>
    public class AdminAuthFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var session = context.HttpContext.Session;
            var userID = session.GetString(SessionKeys.UserID);

            if (string.IsNullOrEmpty(userID))
            {
                // Not logged in — redirect to login page
                context.Result = new RedirectToActionResult("Index", "Login", null);
            }
        }

        // Nothing needed after the action executes
        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    // =========================================================================
    // SuperAdminOnlyFilter
    // Allows only users with Role = "SuperAdmin".
    // RestaurantOwner users who somehow reach a SuperAdmin route get a 403.
    // Apply with: [TypeFilter(typeof(SuperAdminOnlyFilter))]
    // =========================================================================

    /// <summary>
    /// Blocks anyone who is not a SuperAdmin.
    /// Unauthenticated users are sent to /Login.
    /// RestaurantOwner users are shown a 403 Forbidden result.
    ///
    /// Usage:
    ///   [TypeFilter(typeof(SuperAdminOnlyFilter))]
    ///   public class AdminController : Controller { ... }
    /// </summary>
    public class SuperAdminOnlyFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var session = context.HttpContext.Session;
            var userID = session.GetString(SessionKeys.UserID);
            var role = session.GetString(SessionKeys.Role);

            if (string.IsNullOrEmpty(userID))
            {
                // Not logged in at all
                context.Result = new RedirectToActionResult("Index", "Login", null);
                return;
            }

            if (role != "SuperAdmin")
            {
                // Logged in but wrong role — show 403 instead of redirecting,
                // so RestaurantOwners can't probe SuperAdmin URLs by watching redirects.
                context.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                    Content = "403 — You do not have permission to access this page.",
                    ContentType = "text/plain"
                };
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}