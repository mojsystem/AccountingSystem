#!/usr/bin/env python3
"""Smoke test for the running web app (used by CI).

Checks that:
  * the login page is served and the app starts against the database,
  * anonymous requests to protected pages are redirected to the login page,
  * an account can sign in,
  * static files (the stylesheet) are served,
  * the main pages render for the signed-in account, including the Admin-only pages,
  * the Excel exports return an .xlsx package (a zip file, which starts with "PK").

Usage:
  SMOKE_USERNAME=... SMOKE_PASSWORD=... python3 .github/scripts/web-smoke.py <base-url> [log-file]
"""
import html
import http.cookiejar
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

PROTECTED_PAGES = ["/", "/Trade", "/Rates", "/Cash", "/Journal", "/Users", "/Branches"]
EXCEL_EXPORTS = ["/Trade?handler=Excel", "/Journal?handler=Excel"]
STYLESHEET = "/css/site.css"
STARTUP_TIMEOUT_SECONDS = 120
TOKEN_INPUT = re.compile(r'<input[^>]*name="__RequestVerificationToken"[^>]*>')
VALUE_ATTRIBUTE = re.compile(r'value="([^"]*)"')


class Client:
    """HTTP client with its own cookie jar; redirects are followed."""

    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, path: str, form: dict | None = None) -> tuple[int, str, str]:
        """Returns (status code, final URL path, body)."""
        data = urllib.parse.urlencode(form).encode("utf-8") if form is not None else None
        request = urllib.request.Request(self.base_url + path, data=data)
        try:
            with self.opener.open(request, timeout=30) as response:
                body = response.read().decode("utf-8", errors="replace")
                return response.status, urllib.parse.urlparse(response.geturl()).path, body
        except urllib.error.HTTPError as error:
            return error.code, urllib.parse.urlparse(error.url).path, ""


def wait_until_ready(base_url: str) -> None:
    deadline = time.monotonic() + STARTUP_TIMEOUT_SECONDS
    while True:
        try:
            status, _, _ = Client(base_url).request("/Account/Login")
            if status == 200:
                return
            if status >= 500:
                raise RuntimeError(f"the login page returned HTTP {status}")
        except OSError:
            pass  # the server is not accepting connections yet
        if time.monotonic() > deadline:
            raise RuntimeError("the web app did not respond within the startup timeout")
        time.sleep(2)


def antiforgery_token(page: str) -> str:
    tag = TOKEN_INPUT.search(page)
    if tag is None:
        raise RuntimeError("the anti-forgery token was not found on the login page")
    value = VALUE_ATTRIBUTE.search(tag.group(0))
    if value is None:
        raise RuntimeError("the anti-forgery token has no value")
    return html.unescape(value.group(1))


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: web-smoke.py <base-url> [log-file]")
        return 2
    base_url = sys.argv[1]
    log_path = sys.argv[2] if len(sys.argv) > 2 else None
    username = os.environ.get("SMOKE_USERNAME", "")
    password = os.environ.get("SMOKE_PASSWORD", "")
    if not username or not password:
        print("::error title=Web smoke test::SMOKE_USERNAME and SMOKE_PASSWORD must be set")
        return 2

    failures: list[str] = []

    def check(condition: bool, description: str) -> None:
        print(("PASS: " if condition else "FAIL: ") + description)
        if not condition:
            failures.append(description)

    try:
        wait_until_ready(base_url)

        status, path, _ = Client(base_url).request("/Trade")
        check(status == 200 and path == "/Account/Login", "anonymous request to /Trade goes to the login page")

        client = Client(base_url)
        _, _, login_page = client.request("/Account/Login")
        token = antiforgery_token(login_page)
        status, path, _ = client.request(
            "/Account/Login",
            {"Input.Username": username, "Input.Password": password, "__RequestVerificationToken": token},
        )
        check(status == 200 and path in ("/", "/Index"), "sign-in with the test admin account succeeds")

        status, path, _ = Client(base_url).request(STYLESHEET)
        check(status == 200, f"static file {STYLESHEET} is served (HTTP {status})")

        for page_path in PROTECTED_PAGES:
            status, path, _ = client.request(page_path)
            check(status == 200 and path == page_path, f"signed-in GET {page_path} renders (HTTP {status})")

        for export_path in EXCEL_EXPORTS:
            status, _, body = client.request(export_path)
            check(status == 200 and body.startswith("PK"), f"signed-in GET {export_path} returns an .xlsx file (HTTP {status})")
    except RuntimeError as error:
        failures.append(str(error))
        print(f"FAIL: {error}")

    if failures:
        print(f"::error title=Web smoke test::{len(failures)} check(s) failed: " + "; ".join(failures))
        if log_path and os.path.exists(log_path):
            with open(log_path, encoding="utf-8", errors="replace") as log:
                tail = "".join(log.readlines()[-60:])
            print("--- web app log (tail) ---")
            print(tail)
            escaped = tail.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            print(f"::error title=Web app log (tail)::{escaped[:6000]}")
        return 1

    print("::notice title=Web smoke test::all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
