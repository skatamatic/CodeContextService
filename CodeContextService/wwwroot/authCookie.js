// Persist auth token in cookie so full page reload (after login) keeps user logged in
window.setAuthToken = function (token) {
    var maxAge = 60 * 60 * 24; // 1 day in seconds
    document.cookie = 'authToken=' + encodeURIComponent(token) + '; path=/; max-age=' + maxAge + '; SameSite=Lax';
};
window.clearAuthToken = function () {
    document.cookie = 'authToken=; path=/; max-age=0';
};
