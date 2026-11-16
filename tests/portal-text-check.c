// Isolated native clipboard transport: mocks D-Bus and uses a private pipe, never the desktop clipboard.
#include <gio/gio.h>
#include <gio/gunixfdlist.h>
#include <assert.h>
#include <unistd.h>
#include <string.h>

static int reader = -1;
static int offered, completed;
static gboolean completion_ok, reject_write;
static GVariant *mock_call(GDBusConnection *connection, const gchar *name, const gchar *path,
    const gchar *interface, const gchar *method, GVariant *parameters, const GVariantType *reply,
    GDBusCallFlags flags, gint timeout, GCancellable *cancel, GError **error);
static GVariant *mock_write(GDBusConnection *connection, const gchar *name, const gchar *path,
    const gchar *interface, const gchar *method, GVariant *parameters, const GVariantType *reply,
    GDBusCallFlags flags, gint timeout, GUnixFDList *input, GUnixFDList **output,
    GCancellable *cancel, GError **error);
#define g_dbus_connection_call_sync mock_call
#define g_dbus_connection_call_with_unix_fd_list_sync mock_write
#define main portal_helper_main
#include "../src/AxmolHub/Platforms/Linux/portal-helper.c"
#undef main

static GVariant *mock_call(GDBusConnection *connection, const gchar *name, const gchar *path,
    const gchar *interface, const gchar *method, GVariant *parameters, const GVariantType *reply,
    GDBusCallFlags flags, gint timeout, GCancellable *cancel, GError **error)
{
    (void)connection; (void)name; (void)path; (void)reply; (void)flags; (void)timeout; (void)cancel; (void)error;
    assert(!strcmp(interface, CB));
    if (!strcmp(method, "SetSelection")) offered++;
    else if (!strcmp(method, "SelectionWriteDone")) {
        const char *handle; guint32 serial;
        g_variant_get(parameters, "(&oub)", &handle, &serial, &completion_ok);
        assert(!strcmp(handle, session) && serial == 42); completed++;
    } else assert(FALSE);
    g_variant_ref_sink(parameters); g_variant_unref(parameters);
    return g_variant_ref_sink(g_variant_new("()"));
}
static GVariant *mock_write(GDBusConnection *connection, const gchar *name, const gchar *path,
    const gchar *interface, const gchar *method, GVariant *parameters, const GVariantType *reply,
    GDBusCallFlags flags, gint timeout, GUnixFDList *input, GUnixFDList **output,
    GCancellable *cancel, GError **error)
{
    (void)connection; (void)name; (void)path; (void)reply; (void)flags; (void)timeout; (void)input; (void)cancel;
    assert(!strcmp(interface, CB) && !strcmp(method, "SelectionWrite"));
    g_variant_ref_sink(parameters); g_variant_unref(parameters);
    if (reject_write) return NULL;
    int fds[2]; assert(pipe(fds) == 0); reader = fds[0];
    *output = g_unix_fd_list_new();
    int index = g_unix_fd_list_append(*output, fds[1], error); close(fds[1]);
    assert(index >= 0);
    return g_variant_ref_sink(g_variant_new("(h)", index));
}
static void transfer(const char *handle, const char *mime)
{
    GVariant *args = g_variant_ref_sink(g_variant_new("(osu)", handle, mime, 42u));
    clipboard_transfer(NULL, NULL, NULL, NULL, NULL, args, NULL);
    g_variant_unref(args);
}
int main(void)
{
    session = g_strdup("/fixture/session");
    const char *expected = "ASCII [\xe4\xb8\xad\xe6\x96\x87\xf0\x9f\x98\x80]\nline 2\tEND";
    char *encoded = g_base64_encode((const guchar *)expected, strlen(expected));
    assert(!stage_text(encoded) && offered == 0);
    clipboard_enabled = TRUE;
    assert(!stage_text("not-base64!") && offered == 0);
    assert(!stage_text("AA==") && offered == 0);
    assert(!stage_text("/w==") && offered == 0);
    assert(stage_text(encoded) && offered == 1);
    transfer("/fixture/foreign", "text/plain"); assert(completed == 0);
    transfer(session, "text/plain;charset=utf-8");
    assert(text_transferred && !text_failed && completed == 1 && completion_ok);
    char received[256] = {0};
    ssize_t length = read(reader, received, sizeof(received)); close(reader);
    assert(length == (ssize_t)strlen(expected) && !memcmp(received, expected, length));
    assert(stage_text(encoded));
    reject_write = TRUE;
    transfer(session, "text/plain");
    assert(!text_transferred && text_failed && !completion_ok);
    GVariant *details = g_variant_new_parsed("{'session_is_owner': <%b>}", FALSE);
    GVariant *changed = g_variant_ref_sink(g_variant_new("(o@a{sv})", session, details));
    clipboard_changed(NULL, NULL, NULL, NULL, NULL, changed, NULL);
    g_variant_unref(changed);
    assert(!clipboard_text && clipboard_length == 0);
    g_free(encoded); g_free(session);
    puts("PASS: UTF-8 Chinese/emoji/multiline bytes, denied capability, invalid text, foreign session, transfer failure and owner loss; no desktop access.");
    return 0;
}
