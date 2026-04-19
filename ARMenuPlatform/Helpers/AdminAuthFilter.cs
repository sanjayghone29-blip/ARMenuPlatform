using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ARMenuPlatform.Helpers
{
    public static class SessionKeys
    {
        public const string UserID = "UserID";
        public const string Role = "Role";
        public const string FullName = "FullName";
        public const string RestaurantID = "RestaurantID";
    }

    public class AdminAuthFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var userID = context.HttpContext.Session.GetString(SessionKeys.UserID);
            if (string.IsNullOrEmpty(userID))
                context.Result = new RedirectToActionResult("Index", "Login", null);
        }
        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    public class SuperAdminOnlyFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var userID = context.HttpContext.Session.GetString(SessionKeys.UserID);
            var role = context.HttpContext.Session.GetString(SessionKeys.Role);
            if (string.IsNullOrEmpty(userID))
            { context.Result = new RedirectToActionResult("Index", "Login", null); return; }
            if (role != "SuperAdmin")
                context.Result = new ContentResult { StatusCode = StatusCodes.Status403Forbidden, Content = "403 — No permission.", ContentType = "text/plain" };
        }
        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    public class RestaurantOwnerFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var userID = context.HttpContext.Session.GetString(SessionKeys.UserID);
            var role = context.HttpContext.Session.GetString(SessionKeys.Role);
            if (string.IsNullOrEmpty(userID))
            { context.Result = new RedirectToActionResult("Index", "Login", null); return; }
            if (role != "RestaurantOwner")
                context.Result = new ContentResult { StatusCode = StatusCodes.Status403Forbidden, Content = "403 — Restaurant owners only.", ContentType = "text/plain" };
        }
        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}