// Linux-only portal client. GIO and PipeWire are provided by the desktop, not bundled with Hub.
#include <gio/gio.h>
#include <gio/gunixfdlist.h>
#include <glib-unix.h>
#include <pipewire/pipewire.h>
#include <spa/param/video/format-utils.h>
#include <spa/pod/builder.h>
#include <spa/buffer/meta.h>
#include "portal-frame.h"
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <signal.h>

#define SERVICE "org.freedesktop.portal.Desktop"
#define PATH "/org/freedesktop/portal/desktop"
#define RD "org.freedesktop.portal.RemoteDesktop"
#define SC "org.freedesktop.portal.ScreenCast"

static GDBusConnection *bus;
static char *owner, *session;
static GMainLoop *loop;
static struct pw_thread_loop *pwloop;
static struct pw_stream *stream;
static struct spa_hook stream_listener;
static struct spa_video_info_raw format;
static pthread_mutex_t pixels_lock = PTHREAD_MUTEX_INITIALIZER;
static uint8_t *pixels;
static uint32_t width, height, logical_width, logical_height, node, devices;
static uint64_t sequence, requested_sequence, geometry_revision;
static struct hub_frame_geometry geometry;
static gint64 deadline;
static gboolean grabbing;
static gint broken;
static GMainLoop *pending_loop;

static gboolean stop(gpointer data)
{
    (void)data;
    g_atomic_int_set(&broken, TRUE);
    if (pending_loop) g_main_loop_quit(pending_loop);
    if (loop) g_main_loop_quit(loop);
    return G_SOURCE_CONTINUE;
}

static gboolean parent_closed(gint fd, GIOCondition condition, gpointer data)
{
    (void)fd; (void)condition; (void)data;
    stop(NULL);
    return G_SOURCE_REMOVE;
}

static void fail(const char *message)
{
    // stdout is a private framed protocol, not a log. Errors contain no desktop content.
    printf("ERROR %s\n", message);
    fflush(stdout);
    if (loop) g_main_loop_quit(loop);
    g_atomic_int_set(&broken, TRUE);
}

static GVariant *options(const char *token)
{
    GVariantBuilder b;
    g_variant_builder_init(&b, G_VARIANT_TYPE_VARDICT);
    if (token) g_variant_builder_add(&b, "{sv}", "handle_token", g_variant_new_string(token));
    return g_variant_builder_end(&b);
}

static GVariant *call(const char *path, const char *iface, const char *method, GVariant *args)
{
    GError *error = NULL;
    GVariant *result = g_dbus_connection_call_sync(bus, owner, path, iface, method,
        args, NULL, G_DBUS_CALL_FLAGS_NONE, 5000, NULL, &error);
    if (!result) {
        fprintf(stderr, "Portal %s failed: %s\n", method, error ? error->message : "unknown error");
        g_clear_error(&error);
    }
    return result;
}

static uint32_t property(const char *iface, const char *name)
{
    GVariant *r = call(PATH, "org.freedesktop.DBus.Properties", "Get", g_variant_new("(ss)", iface, name));
    if (!r) return 0;
    GVariant *v, *value;
    g_variant_get(r, "(@v)", &v);
    value = g_variant_get_variant(v);
    uint32_t n = g_variant_is_of_type(value, G_VARIANT_TYPE_UINT32) ? g_variant_get_uint32(value) : 0;
    g_variant_unref(value); g_variant_unref(v); g_variant_unref(r);
    return n;
}

struct pending { GMainLoop *wait; GVariant *results; uint32_t response; };
static void response(GDBusConnection *c, const gchar *sender, const gchar *path,
    const gchar *iface, const gchar *signal, GVariant *args, gpointer data)
{
    (void)c; (void)sender; (void)path; (void)iface; (void)signal;
    struct pending *p = data;
    if (!g_variant_is_of_type(args, G_VARIANT_TYPE("(ua{sv})"))) return;
    g_variant_get(args, "(u@a{sv})", &p->response, &p->results);
    g_main_loop_quit(p->wait);
}
static gboolean expired(gpointer data)
{
    g_main_loop_quit(((struct pending *)data)->wait);
    return G_SOURCE_REMOVE;
}

static GVariant *request(const char *iface, const char *method, const char *token, GVariant *args)
{
    char *sender = g_strdup(g_dbus_connection_get_unique_name(bus) + 1);
    for (char *p = sender; *p; ++p) if (*p == '.') *p = '_';
    char *path = g_strdup_printf("/org/freedesktop/portal/desktop/request/%s/%s", sender, token);
    g_free(sender);
    struct pending p = { g_main_loop_new(NULL, FALSE), NULL, 2 };
    guint watch = g_dbus_connection_signal_subscribe(bus, owner, "org.freedesktop.portal.Request",
        "Response", path, NULL, G_DBUS_SIGNAL_FLAGS_NONE, response, &p, NULL);
    GVariant *r = call(PATH, iface, method, args);
    if (r) {
        const char *returned;
        g_variant_get(r, "(&o)", &returned);
        if (strcmp(returned, path) != 0) { g_variant_unref(r); r = NULL; }
    }
    guint timer = g_timeout_add_seconds(120, expired, &p);
    pending_loop = p.wait;
    if (r && !p.results && !g_atomic_int_get(&broken)) g_main_loop_run(p.wait);
    pending_loop = NULL;
    if (g_main_context_find_source_by_id(NULL, timer)) g_source_remove(timer);
    if (!p.results && r) {
        GVariant *closed = call(path, "org.freedesktop.portal.Request", "Close", NULL);
        if (closed) g_variant_unref(closed);
    }
    g_dbus_connection_signal_unsubscribe(bus, watch);
    g_main_loop_unref(p.wait); g_free(path);
    if (r) g_variant_unref(r);
    if (p.response != 0) { if (p.results) g_variant_unref(p.results); return NULL; }
    return p.results;
}

static void session_closed(GDBusConnection *c, const gchar *sender, const gchar *path,
    const gchar *iface, const gchar *signal, GVariant *args, gpointer data)
{
    (void)c; (void)sender; (void)path; (void)iface; (void)signal; (void)args; (void)data;
    stop(NULL);
}

static void state_changed(void *data, enum pw_stream_state old, enum pw_stream_state state, const char *error)
{
    (void)data; (void)old; (void)error;
    if (state == PW_STREAM_STATE_ERROR || state == PW_STREAM_STATE_UNCONNECTED) g_atomic_int_set(&broken, TRUE);
}
static void param_changed(void *data, uint32_t id, const struct spa_pod *param)
{
    (void)data;
    if (id != SPA_PARAM_Format || !param) return;
    struct spa_video_info_raw next = {0};
    if (spa_format_video_raw_parse(param, &next) < 0
        || (next.format != SPA_VIDEO_FORMAT_BGRx && next.format != SPA_VIDEO_FORMAT_BGRA
            && next.format != SPA_VIDEO_FORMAT_RGBx && next.format != SPA_VIDEO_FORMAT_RGBA))
    { g_atomic_int_set(&broken, TRUE); return; }
    pthread_mutex_lock(&pixels_lock);
    // A new format after negotiation changes the screenshot/input mapping. End the session instead of
    // reusing logical coordinates captured when the portal was started.
    if (format.size.width && (format.size.width != next.size.width || format.size.height != next.size.height
        || format.format != next.format)) g_atomic_int_set(&broken, TRUE);
    format = next;
    g_clear_pointer(&pixels, g_free);
    width = height = 0;
    ++geometry_revision;
    pthread_mutex_unlock(&pixels_lock);

    // Metadata is negotiated explicitly; without this Mutter may provide only the fixed monitor-sized canvas.
    uint8_t storage[512];
    struct spa_pod_builder builder = SPA_POD_BUILDER_INIT(storage, sizeof(storage));
    const struct spa_pod *params[] = {
        spa_pod_builder_add_object(&builder, SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
            SPA_PARAM_META_type, SPA_POD_Id(SPA_META_VideoCrop),
            SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_region))),
        spa_pod_builder_add_object(&builder, SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
            SPA_PARAM_META_type, SPA_POD_Id(SPA_META_VideoTransform),
            SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_videotransform))),
        spa_pod_builder_add_object(&builder, SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
            SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
            SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header))),
    };
    pw_stream_update_params(stream, params, G_N_ELEMENTS(params));
}
static void process(void *data)
{
    (void)data;
    struct pw_buffer *b, *latest = NULL;
    while ((b = pw_stream_dequeue_buffer(stream))) {
        if (latest) pw_stream_queue_buffer(stream, latest);
        latest = b;
    }
    if (!latest) return;
    struct spa_buffer *buffer = latest->buffer;
    if (buffer->n_datas < 1) goto done;
    struct spa_data *d = &buffer->datas[0];
    if (!d->data || !d->chunk || (d->chunk->flags & (SPA_CHUNK_FLAG_CORRUPTED | SPA_CHUNK_FLAG_EMPTY))) goto done;
    const struct spa_meta_header *header = spa_buffer_find_meta_data(buffer, SPA_META_Header, sizeof(*header));
    if (header && (header->flags & (SPA_META_HEADER_FLAG_CORRUPTED | SPA_META_HEADER_FLAG_GAP))) goto done;
    struct hub_frame_geometry next = {
        .buffer_width = format.size.width, .buffer_height = format.size.height,
        .width = format.size.width, .height = format.size.height, .format = format.format,
    };
    const struct spa_meta *crop_meta = spa_buffer_find_meta(buffer, SPA_META_VideoCrop);
    if (crop_meta) {
        if (!crop_meta->data || crop_meta->size < sizeof(struct spa_meta_region)) goto invalid;
        const struct spa_meta_region *crop = crop_meta->data;
        if (crop->region.position.x < 0 || crop->region.position.y < 0) goto invalid;
        next.x = crop->region.position.x; next.y = crop->region.position.y;
        next.width = crop->region.size.width; next.height = crop->region.size.height; next.has_crop = 1;
    }
    const struct spa_meta *transform_meta = spa_buffer_find_meta(buffer, SPA_META_VideoTransform);
    if (transform_meta) {
        if (!transform_meta->data || transform_meta->size < sizeof(struct spa_meta_videotransform)) goto invalid;
        next.transform = ((const struct spa_meta_videotransform *)transform_meta->data)->transform;
    }
    if (!hub_frame_valid(&next)) goto invalid;
    uint64_t length = (uint64_t)next.width * next.height * 4;
    pthread_mutex_lock(&pixels_lock);
    pixels = g_realloc(pixels, length);
    if (!hub_frame_copy(&next, d->data, d->maxsize, d->chunk->offset, d->chunk->size, d->chunk->stride,
            format.format == SPA_VIDEO_FORMAT_RGBx || format.format == SPA_VIDEO_FORMAT_RGBA, pixels)) {
        pthread_mutex_unlock(&pixels_lock); goto invalid;
    }
    if (memcmp(&geometry, &next, sizeof(next))) { geometry = next; ++geometry_revision; }
    width = hub_frame_width(&next); height = hub_frame_height(&next); ++sequence;
    pthread_mutex_unlock(&pixels_lock);
    goto done;
invalid:
    // Invalid/empty crop is not a request to expose the full canvas or reuse an older frame.
    pthread_mutex_lock(&pixels_lock);
    g_clear_pointer(&pixels, g_free); ++geometry_revision;
    pthread_mutex_unlock(&pixels_lock);
done:
    pw_stream_queue_buffer(stream, latest);
}
static const struct pw_stream_events events = {
    PW_VERSION_STREAM_EVENTS, .state_changed = state_changed, .param_changed = param_changed, .process = process
};

static gboolean tick(gpointer data)
{
    (void)data;
    if (g_atomic_int_get(&broken)) { fail("stream-disconnected"); return G_SOURCE_REMOVE; }
    if (g_get_monotonic_time() > deadline) { fail(grabbing ? "frame-timeout" : "session-expired"); return G_SOURCE_REMOVE; }
    if (!grabbing) return G_SOURCE_CONTINUE;
    uint32_t w = 0, h = 0, length = 0;
    uint64_t revision = 0;
    uint8_t *frame = NULL;
    pthread_mutex_lock(&pixels_lock);
    if (pixels && sequence > requested_sequence) {
        w = width; h = height; length = width * height * 4;
        frame = g_memdup2(pixels, length); revision = geometry_revision;
    }
    pthread_mutex_unlock(&pixels_lock);
    if (frame) {
        printf("FRAME2 %u %u %u %u %u %" G_GUINT64_FORMAT "\n", w, h, logical_width, logical_height, length, revision);
        gboolean sent = fwrite(frame, 1, length, stdout) == length;
        fflush(stdout);
        g_free(frame);
        if (!sent) { g_atomic_int_set(&broken, TRUE); return G_SOURCE_REMOVE; }
        grabbing = FALSE;
        deadline = g_get_monotonic_time() + 120 * G_USEC_PER_SEC;
    }
    return G_SOURCE_CONTINUE;
}

static gboolean notify(const char *method, GVariant *args)
{
    GVariant *r = call(session ? PATH : "", RD, method, args);
    if (!r) return FALSE;
    g_variant_unref(r); return TRUE;
}
static gboolean key(uint32_t sym)
{
    gboolean down = notify("NotifyKeyboardKeysym", g_variant_new("(o@a{sv}iu)", session, options(NULL), sym, 1u));
    // Release even when the press failed: partial delivery must never leave a held key behind.
    gboolean up = notify("NotifyKeyboardKeysym", g_variant_new("(o@a{sv}iu)", session, options(NULL), sym, 0u));
    return down && up;
}
static gboolean command(gint fd, GIOCondition condition, gpointer data)
{
    (void)fd; (void)data;
    if (g_atomic_int_get(&broken)) { fail("stream-disconnected"); return G_SOURCE_REMOVE; }
    if (condition & (G_IO_HUP | G_IO_ERR)) { g_main_loop_quit(loop); return G_SOURCE_REMOVE; }
    char line[128];
    if (!fgets(line, sizeof(line), stdin)) { g_main_loop_quit(loop); return G_SOURCE_REMOVE; }
    deadline = g_get_monotonic_time() + 120 * G_USEC_PER_SEC;
    if (!strcmp(line, "GRAB\n")) {
        pthread_mutex_lock(&pixels_lock); requested_sequence = sequence; pthread_mutex_unlock(&pixels_lock);
        grabbing = TRUE; deadline = g_get_monotonic_time() + 8 * G_USEC_PER_SEC;
        return G_SOURCE_CONTINUE;
    }
    if (!strcmp(line, "CLOSE\n")) { g_main_loop_quit(loop); return G_SOURCE_REMOVE; }
    unsigned sym, x, y; int steps;
    guint64 revision = 0;
    double lx, ly;
    gboolean ok = FALSE;
    // The mapping is held through delivery so a concurrent crop change cannot redirect a confirmed click.
    pthread_mutex_lock(&pixels_lock);
    gboolean is_key = sscanf(line, "KEY %" G_GUINT64_FORMAT " %u", &revision, &sym) == 2;
    gboolean is_click = sscanf(line, "CLICK %" G_GUINT64_FORMAT " %u %u", &revision, &x, &y) == 3;
    gboolean is_scroll = sscanf(line, "SCROLL %" G_GUINT64_FORMAT " %d", &revision, &steps) == 2;
    if (!pixels || revision != geometry_revision || g_atomic_int_get(&broken)) {
        pthread_mutex_unlock(&pixels_lock);
        fail("frame-mapping-changed"); return G_SOURCE_REMOVE;
    }
    if (is_key && sym <= 0x11ffffff && (devices & 1)) ok = key(sym);
    else if (is_click && (devices & 2)
        && hub_frame_logical_point(&geometry, logical_width, logical_height, x, y, &lx, &ly)) {
        ok = notify("NotifyPointerMotionAbsolute", g_variant_new("(o@a{sv}udd)", session, options(NULL), node, lx, ly));
        if (ok) {
            gboolean down = notify("NotifyPointerButton", g_variant_new("(o@a{sv}iu)", session, options(NULL), 272, 1u));
            gboolean up = notify("NotifyPointerButton", g_variant_new("(o@a{sv}iu)", session, options(NULL), 272, 0u));
            ok = down && up;
        }
    } else if (is_scroll && steps >= -10 && steps <= 10 && steps && (devices & 2)
        && hub_frame_logical_point(&geometry, logical_width, logical_height,
            hub_frame_width(&geometry) / 2, hub_frame_height(&geometry) / 2, &lx, &ly)) {
        ok = notify("NotifyPointerMotionAbsolute", g_variant_new("(o@a{sv}udd)", session, options(NULL), node, lx, ly))
            && notify("NotifyPointerAxisDiscrete", g_variant_new("(o@a{sv}ui)", session, options(NULL), 0u, -steps));
    }
    pthread_mutex_unlock(&pixels_lock);
    if (!ok) { fail("input-unavailable-or-failed"); return G_SOURCE_REMOVE; }
    puts("OK"); fflush(stdout); return G_SOURCE_CONTINUE;
}

int main(int argc, char **argv)
{
    setvbuf(stdin, NULL, _IONBF, 0);
    GError *error = NULL;
    g_unix_signal_add(SIGTERM, stop, NULL);
    g_unix_signal_add(SIGINT, stop, NULL);
    signal(SIGPIPE, SIG_IGN);
    // Watch before consent as well: a terminated Hub must not leave a selection dialog/session behind.
    g_unix_fd_add(STDIN_FILENO, G_IO_HUP | G_IO_ERR, parent_closed, NULL);
    gchar *address = g_dbus_address_get_for_bus_sync(G_BUS_TYPE_SESSION, NULL, &error);
    bus = address ? g_dbus_connection_new_for_address_sync(address,
        G_DBUS_CONNECTION_FLAGS_AUTHENTICATION_CLIENT | G_DBUS_CONNECTION_FLAGS_MESSAGE_BUS_CONNECTION,
        NULL, NULL, &error) : NULL;
    g_free(address);
    if (!bus) { fail("session-bus-unavailable"); return 1; }
    GVariant *r = g_dbus_connection_call_sync(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus",
        "org.freedesktop.DBus", "StartServiceByName", g_variant_new("(su)", SERVICE, 0u), NULL,
        G_DBUS_CALL_FLAGS_NONE, 5000, NULL, &error);
    if (r) g_variant_unref(r);
    g_clear_error(&error);
    r = g_dbus_connection_call_sync(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
        "GetNameOwner", g_variant_new("(s)", SERVICE), G_VARIANT_TYPE("(s)"), G_DBUS_CALL_FLAGS_NONE, 5000, NULL, &error);
    if (!r) { fail("portal-unavailable"); return 1; }
    g_variant_get(r, "(s)", &owner); g_variant_unref(r);
    if (property(RD, "version") < 1) { fail("remotedesktop-unavailable"); return 1; }
    if (property(SC, "version") < 3 || !(property(SC, "AvailableSourceTypes") & 2)) { fail("window-sources-unavailable"); return 1; }
    uint32_t available_devices = property(RD, "AvailableDeviceTypes") & 3;
    if (!available_devices) { fail("input-devices-unavailable"); return 1; }
    if (argc == 2 && !strcmp(argv[1], "--probe")) { puts("READY"); return 0; }

    GVariantBuilder b;
    g_variant_builder_init(&b, G_VARIANT_TYPE_VARDICT);
    g_variant_builder_add(&b, "{sv}", "handle_token", g_variant_new_string("hub_create"));
    g_variant_builder_add(&b, "{sv}", "session_handle_token", g_variant_new_string("hub_session"));
    r = request(RD, "CreateSession", "hub_create", g_variant_new("(@a{sv})", g_variant_builder_end(&b)));
    if (!r || !g_variant_lookup(r, "session_handle", "s", &session)) {
        fail("session-create-failed-or-cancelled"); if (r) g_variant_unref(r); goto cleanup;
    }
    g_variant_unref(r);
    g_dbus_connection_signal_subscribe(bus, owner, "org.freedesktop.portal.Session", "Closed", session, NULL,
        G_DBUS_SIGNAL_FLAGS_NONE, session_closed, NULL, NULL);
    g_variant_builder_init(&b, G_VARIANT_TYPE_VARDICT);
    g_variant_builder_add(&b, "{sv}", "handle_token", g_variant_new_string("hub_devices"));
    g_variant_builder_add(&b, "{sv}", "types", g_variant_new_uint32(available_devices));
    if (property(RD, "version") >= 2) g_variant_builder_add(&b, "{sv}", "persist_mode", g_variant_new_uint32(0));
    r = request(RD, "SelectDevices", "hub_devices", g_variant_new("(o@a{sv})", session, g_variant_builder_end(&b)));
    if (!r) { fail("device-selection-failed-or-cancelled"); goto cleanup; } g_variant_unref(r);
    g_variant_builder_init(&b, G_VARIANT_TYPE_VARDICT);
    g_variant_builder_add(&b, "{sv}", "handle_token", g_variant_new_string("hub_sources"));
    g_variant_builder_add(&b, "{sv}", "types", g_variant_new_uint32(2));
    g_variant_builder_add(&b, "{sv}", "multiple", g_variant_new_boolean(FALSE));
    r = request(SC, "SelectSources", "hub_sources", g_variant_new("(o@a{sv})", session, g_variant_builder_end(&b)));
    if (!r) { fail("source-selection-failed-or-cancelled"); goto cleanup; } g_variant_unref(r);
    r = request(RD, "Start", "hub_start", g_variant_new("(os@a{sv})", session, "", options("hub_start")));
    if (!r) { fail("session-start-failed-or-cancelled"); goto cleanup; }
    g_variant_lookup(r, "devices", "u", &devices);
    GVariant *streams = g_variant_lookup_value(r, "streams", G_VARIANT_TYPE("a(ua{sv})"));
    if (!streams || g_variant_n_children(streams) != 1) {
        fail("expected-single-window"); if (streams) g_variant_unref(streams); g_variant_unref(r); goto cleanup;
    }
    GVariant *props, *item = g_variant_get_child_value(streams, 0);
    g_variant_get(item, "(u@a{sv})", &node, &props);
    uint32_t type = 0; uint64_t serial = 0; int32_t lw = 0, lh = 0;
    g_variant_lookup(props, "source_type", "u", &type);
    g_variant_lookup(props, "pipewire-serial", "t", &serial);
    g_variant_lookup(props, "size", "(ii)", &lw, &lh);
    if (lw > 0 && lh > 0) { logical_width = lw; logical_height = lh; }
    g_variant_unref(props); g_variant_unref(item); g_variant_unref(streams); g_variant_unref(r);
    if (type != 2) { fail("non-window-source-refused"); goto cleanup; }

    GUnixFDList *fds = NULL;
    r = g_dbus_connection_call_with_unix_fd_list_sync(bus, owner, PATH, SC, "OpenPipeWireRemote",
        g_variant_new("(o@a{sv})", session, options(NULL)), G_VARIANT_TYPE("(h)"), G_DBUS_CALL_FLAGS_NONE,
        5000, NULL, &fds, NULL, &error);
    if (!r) { fail("pipewire-remote-unavailable"); g_clear_error(&error); goto cleanup; }
    int index; g_variant_get(r, "(h)", &index); g_variant_unref(r);
    int fd = g_unix_fd_list_get(fds, index, &error); g_object_unref(fds);
    if (fd < 0) { fail("pipewire-fd-unavailable"); goto cleanup; }
    pw_init(NULL, NULL);
    pwloop = pw_thread_loop_new("hub-portal", NULL);
    if (!pwloop) { close(fd); fail("pipewire-loop-failed"); goto cleanup; }
    struct pw_context *context = pw_context_new(pw_thread_loop_get_loop(pwloop), NULL, 0);
    if (!context) { close(fd); fail("pipewire-context-failed"); goto cleanup; }
    struct pw_core *core = pw_context_connect_fd(context, fd, NULL, 0);
    if (!core) { fail("pipewire-connect-failed"); goto cleanup; }
    struct pw_properties *p = pw_properties_new(PW_KEY_MEDIA_TYPE, "Video", PW_KEY_MEDIA_CATEGORY, "Capture", PW_KEY_MEDIA_ROLE, "Screen", NULL);
    if (serial) { char value[32]; snprintf(value, sizeof(value), "%lu", (unsigned long)serial); pw_properties_set(p, PW_KEY_TARGET_OBJECT, value); }
    stream = pw_stream_new(core, "Axmol Hub portal window", p);
    if (!stream) { fail("pipewire-stream-failed"); goto cleanup; }
    pw_stream_add_listener(stream, &stream_listener, &events, NULL);
    uint8_t pod_buffer[1024];
    struct spa_pod_builder builder = SPA_POD_BUILDER_INIT(pod_buffer, sizeof(pod_buffer));
    const struct spa_pod *params[] = { spa_pod_builder_add_object(&builder, SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video), SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_CHOICE_ENUM_Id(4, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_RGBx, SPA_VIDEO_FORMAT_RGBA)) };
    if (pw_stream_connect(stream, PW_DIRECTION_INPUT, serial ? PW_ID_ANY : node,
        PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS, params, 1) < 0) { fail("pipewire-stream-failed"); goto cleanup; }
    if (pw_thread_loop_start(pwloop) < 0) { fail("pipewire-loop-failed"); goto cleanup; }
    loop = g_main_loop_new(NULL, FALSE);
    g_unix_fd_add(STDIN_FILENO, G_IO_IN | G_IO_HUP | G_IO_ERR, command, NULL);
    g_timeout_add(20, tick, NULL);
    deadline = g_get_monotonic_time() + 120 * G_USEC_PER_SEC;
    printf("READY %u\n", devices); fflush(stdout);
    if (!g_atomic_int_get(&broken)) g_main_loop_run(loop);
    pw_thread_loop_stop(pwloop);
    pw_stream_destroy(stream); pw_core_disconnect(core); pw_context_destroy(context); pw_thread_loop_destroy(pwloop);
cleanup:
    if (session) { r = call(session, "org.freedesktop.portal.Session", "Close", NULL); if (r) g_variant_unref(r); }
    g_clear_pointer(&pixels, g_free);
    g_object_unref(bus); g_free(owner); g_free(session);
    return g_atomic_int_get(&broken) ? 1 : 0;
}
