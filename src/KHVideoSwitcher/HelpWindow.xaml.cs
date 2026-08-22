using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KHVideoSwitcher;

/// <summary>
/// Plain-English user guide. Non-modal (opened with Show, not ShowDialog) so an
/// operator can keep it open on a second monitor while running the app for real.
/// Content is built in code rather than XAML because it's long, section-based,
/// and needs a nav list wired to each section's position for the jump-to-section
/// behavior below.
/// </summary>
public partial class HelpWindow : Window
{
    private Brush TextBrush = null!;
    private Brush MutedBrush = null!;
    private Brush AccentBrush = null!;
    private Brush Accent100Brush = null!;
    private Brush DividerBrush = null!;
    private FontFamily HeadingFont = null!;
    private FontFamily BodyFont = null!;

    private StackPanel _current = null!;
    private Button? _selectedNavButton;

    public HelpWindow()
    {
        InitializeComponent();

        TextBrush = (Brush)Application.Current.Resources["TextBrush"];
        MutedBrush = (Brush)Application.Current.Resources["MutedTextBrush"];
        AccentBrush = (Brush)Application.Current.Resources["AccentBrush"];
        Accent100Brush = (Brush)Application.Current.Resources["Accent100Brush"];
        DividerBrush = (Brush)Application.Current.Resources["DividerBrush"];
        HeadingFont = (FontFamily)Application.Current.Resources["HeadingFont"];
        BodyFont = (FontFamily)Application.Current.Resources["BodyFont"];

        BuildContent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- content-authoring helpers ----------

    private void Section(string title)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var wrapper = new StackPanel { Margin = new Thickness(0, 0, 0, 38) };

        var heading = new TextBlock
        {
            Text = title,
            FontFamily = HeadingFont,
            FontSize = 23,
            Foreground = TextBrush,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var rule = new Border { Height = 1, Background = DividerBrush, Margin = new Thickness(0, 0, 0, 14) };

        wrapper.Children.Add(heading);
        wrapper.Children.Add(rule);
        wrapper.Children.Add(container);
        ContentPanel.Children.Add(wrapper);
        _current = container;

        var nav = new Button
        {
            Content = title,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = MutedBrush,
            FontFamily = BodyFont,
            FontSize = 13,
            Padding = new Thickness(10, 7, 6, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        nav.Click += (_, _) => ScrollToSection(wrapper, nav);
        NavPanel.Children.Add(nav);
    }

    private void ScrollToSection(FrameworkElement section, Button navButton)
    {
        var transform = section.TransformToAncestor(ContentPanel);
        var position = transform.Transform(new Point(0, 0));
        ContentScroll.ScrollToVerticalOffset(position.Y);

        if (_selectedNavButton is not null)
        {
            _selectedNavButton.Foreground = MutedBrush;
            _selectedNavButton.FontFamily = BodyFont;
        }
        navButton.Foreground = AccentBrush;
        navButton.FontFamily = HeadingFont;
        _selectedNavButton = navButton;
    }

    private void Sub(string text) => _current.Children.Add(new TextBlock
    {
        Text = text,
        FontFamily = HeadingFont,
        FontSize = 15,
        Foreground = TextBrush,
        Margin = new Thickness(0, 18, 0, 6)
    });

    private void Body(string text) => _current.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = BodyFont,
        FontSize = 13.5,
        Foreground = TextBrush,
        LineHeight = 21,
        Margin = new Thickness(0, 0, 0, 10)
    });

    private void Bullets(params string[] items)
    {
        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new TextBlock { Text = "•", Foreground = AccentBrush, FontFamily = BodyFont, FontSize = 13.5, Margin = new Thickness(0, 0, 10, 0) };
            var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, FontFamily = BodyFont, FontSize = 13.5, LineHeight = 20 };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);
            _current.Children.Add(row);
        }
    }

    private void Steps(params string[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = new TextBlock
            {
                Text = $"{i + 1}.",
                Foreground = AccentBrush,
                FontFamily = HeadingFont,
                FontSize = 14,
                Margin = new Thickness(0, 0, 10, 0),
                MinWidth = 20
            };
            var text = new TextBlock { Text = items[i], TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, FontFamily = BodyFont, FontSize = 13.5, LineHeight = 20 };
            Grid.SetColumn(num, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(num);
            row.Children.Add(text);
            _current.Children.Add(row);
        }
    }

    /// <summary>A callout box for tips, examples, and "why this matters" asides.</summary>
    private void Tip(string label, string text)
    {
        var border = new Border
        {
            Background = Accent100Brush,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 4, 0, 14)
        };
        border.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
        border.BorderBrush = AccentBrush;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = HeadingFont,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["Accent700Brush"],
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = BodyFont,
            FontSize = 13,
            Foreground = TextBrush,
            LineHeight = 19
        });
        border.Child = stack;
        _current.Children.Add(border);
    }

    private void ShortcutTable(params (string Key, string Action)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var keyBorder = new Border
            {
                Background = (Brush)Application.Current.Resources["SurfaceBrush"],
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 3, 10, 3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            keyBorder.Child = new TextBlock { Text = rows[i].Key, FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Foreground = TextBrush };

            var action = new TextBlock
            {
                Text = rows[i].Action,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = BodyFont,
                FontSize = 13.5,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3)
            };

            Grid.SetRow(keyBorder, i);
            Grid.SetColumn(keyBorder, 0);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keyBorder);
            grid.Children.Add(action);
        }
        _current.Children.Add(grid);
    }

    // ---------- the actual guide ----------

    private void BuildContent()
    {
        Section("Welcome");
        Body("KH Video Switcher lets one person run a simple, professional-looking video feed for a Kingdom Hall meeting streamed over Zoom — a camera shot of the platform, JW Library's video and picture content, and clean transitions between them. It replaces a room full of production equipment with one laptop.");
        Body("You don't need any video experience to run it. This guide walks through every button, what it's for, and how the pieces fit together, with a few real meeting scenarios at the end. Keep this window open while you work — it doesn't block the main app.");
        Tip("The three things this app does", "1) Takes your webcam and lets you frame and smoothly switch shots. 2) Watches JW Library and can automatically bring video/pictures on screen. 3) Sends the finished picture into Zoom as if it were a webcam, called \"KH Video Switcher\".");

        Section("Quick Start (First Time on a Computer)");
        Body("Do this once per computer. After that, meeting-day startup is just: open the app → Start → Capture → Virtual Camera on.");
        Steps(
            "Open KH Video Switcher, choose your webcam in the camera list (top-left), and click Start.",
            "Open the Media dropdown (next to it) and choose the JW Library window or the monitor/screen that shows JW Library, then click Capture.",
            "With JW Library showing its normal starting screen (the yeartext, i.e. \"nothing playing\"), click the STOCK tag in the status row. This teaches the app to recognize \"nothing is on\" so it doesn't confuse it with a picture. The STOCK tag has a dashed outline until you do this — that's your reminder.",
            "Frame a shot or two in the PREVIEW pane (drag to pan, scroll wheel or the ZOOM slider to zoom in), then right-click a preset button to save it. Save a couple of go-to shots (e.g. a wide platform shot, a closer shot on the speaker).",
            "Click Virtual Camera to turn it on, then in Zoom's video settings choose the camera named \"KH Video Switcher\" instead of your webcam.",
            "Optional but recommended: press F4 to turn on AUTO SCENES so the app switches to video/pictures automatically when JW Library plays them.");
        Tip("You only have to do this once", "The app remembers your camera, your capture source, your presets, and the stock screen fingerprint. Next time you just open the app and click through Start / Capture / Virtual Camera.");

        Section("Meeting Day Checklist");
        Bullets(
            "Open KH Video Switcher.",
            "Click Start (camera) and Capture (JW Library) if they aren't already running.",
            "Turn on Virtual Camera, and make sure Zoom is using \"KH Video Switcher\" as its camera.",
            "Check the status row: AUTO SCENES on if you want automatic switching, STOCK should already show a fingerprint from setup.",
            "Recall your saved presets (keys 1–6) to check framing still looks right, then start the meeting on CAM.");

        Section("The Toolbar");
        Body("The row along the very top controls your two video sources and the app's own output.");
        Sub("Camera dropdown + Start");
        Body("Pick which physical camera feeds the app. Start begins reading from it; the button changes to Stop while running. You must Start the camera before anything appears in PREVIEW or PROGRAM.");
        Sub("Media dropdown + Capture");
        Body("Pick the window or screen that shows JW Library, then click Capture to begin watching it. This is how the app \"sees\" videos, pictures, and songs that JW Library displays, so it can show them to your viewers and (if AUTO SCENES is on) recognize what's currently on screen.");
        Sub("Virtual Camera");
        Body("Turns the app's finished picture (PROGRAM) into a camera that other apps can use — most importantly Zoom. See \"Sending Your Feed to Zoom\" below for the full walkthrough.");
        Sub("Settings");
        Body("Opens a separate window with adjustable options: crossfade timing, the size and position of the over-the-shoulder picture box, and a few toggles. Covered in detail in the Settings section below.");
        Sub("Compact");
        Body("Hides the PREVIEW pane so only PROGRAM (what viewers see) is shown, for a smaller window footprint. Handy on a small screen. Press F11 to toggle it either way.");

        Section("Status Tags");
        Body("The row of small labeled tags just under the toolbar shows, at a glance, whether each feature is on or off. A solid filled tag means ON / engaged; an outlined tag means OFF / idle. Every tag is also a button — click it to toggle that feature directly, without opening Settings.");
        Bullets(
            "VIRTUAL CAM — same as the Virtual Camera button in the toolbar; shown here so you can see its state without looking away.",
            "AUTO SCENES (F4) — when on, the app automatically switches your outgoing shot based on what JW Library is showing: a playing video goes to MEDIA, a still picture goes to OTS (or MEDIA, if you've turned on \"Always Full Screen First\" in Settings), and \"nothing playing\" returns to CAM. You can always override manually at any time.",
            "AUTO-TAKE (A) — when on, pressing a scene button or F1/F2/F3 crossfades PROGRAM immediately. When off, those same controls only stage a shot in PREVIEW, and you press TAKE or CUT yourself to send it live. Turn this off if you want to review a shot before it goes out.",
            "DRAG MODE — when on, dragging inside PREVIEW moves the picture the same direction your mouse moves (feels like grabbing the picture itself). When off, dragging moves the crop frame instead, which is the opposite feel. Either is fine — pick whichever feels natural.",
            "STOCK — shows NOT SET (dashed, needs attention) until you click it while JW Library is on its plain \"nothing playing\" screen. This is a one-time step per computer that lets AUTO SCENES tell \"nothing on\" apart from \"a picture is on.\"",
            "UPDATE — only appears when a newer version of the app is available; click it to download and install.");

        Section("PREVIEW and PROGRAM");
        Body("These two large panes are the heart of the operator view.");
        Bullets(
            "PROGRAM (right) is what your viewers currently see in Zoom. Nothing changes here until you send a shot to it with TAKE or CUT (or automatically, if AUTO-TAKE is on).",
            "PREVIEW (left) is your \"next shot\" workspace — line it up here first, safely, before it goes live. Drag inside it to pan, and use the mouse scroll wheel or the ZOOM slider underneath to zoom in or out.");
        Tip("Why two panes?", "Because you can frame your next shot — zoom in on the speaker, or reposition — without the audience ever seeing you adjust it. Once it looks right in PREVIEW, send it to PROGRAM with TAKE (smooth crossfade) or CUT (instant).");

        Section("Scenes: CAM, MEDIA, OTS");
        Body("A \"scene\" is what kind of picture is currently framed. There are three, shown as the three wide buttons above the presets row (with keyboard shortcuts F1/F2/F3):");
        Bullets(
            "CAM — just your camera, full screen. The everyday shot for a speaker or reader.",
            "MEDIA — JW Library's video or picture, full screen, with no camera visible. Used for video illustrations, cartoons, or (if you've enabled \"Always Full Screen First\" in Settings) any picture too.",
            "OTS (\"over-the-shoulder\") — your camera shot with JW Library's current picture boxed in the top-right corner, like a news broadcast. The size and position of that box are adjustable in Settings. This is the default look for still pictures when AUTO SCENES is on, unless you've turned on Always Full Screen First.");
        Body("Clicking a scene button (or its F-key) stages that scene in PREVIEW. Whether it goes live immediately or waits for TAKE/CUT depends on the AUTO-TAKE status tag described above.");

        Section("TAKE, CUT, and Wide");
        Bullets(
            "TAKE (Enter) — smoothly crossfades PROGRAM to whatever is currently staged in PREVIEW. This is your normal, polished way to switch.",
            "CUT (Space) — instantly switches PROGRAM to PREVIEW with no fade. Useful if you need to switch immediately (e.g. correcting a mistake).",
            "Wide (0) — resets PREVIEW to the full, un-zoomed camera shot. A quick way to get back to a safe, simple shot before deciding what's next.");

        Section("Presets");
        Body("Presets remember a complete shot — both the scene (CAM/MEDIA/OTS) and exactly how the camera was framed — so you can jump back to it instantly during a meeting instead of re-framing by hand.");
        Bullets(
            "Recall a preset: click one of the six preset buttons, or press number keys 1–6. This stages that shot in PREVIEW, same as any other scene change.",
            "Save a preset: frame the shot you want in PREVIEW, then right-click a preset button, or hold Ctrl and press a number key 1–6.",
            "A preset with something saved shows a solid border; an empty one shows a dashed border.");
        Tip("Example", "Save preset 1 as a wide platform shot on CAM, and preset 2 as a closer shot on the lectern. During the meeting, tap 1 or 2 to line up the next angle, then TAKE when ready.");

        Section("Keyboard Shortcuts");
        Body("Every control has a mouse equivalent, but once you're comfortable, the keyboard is faster and lets you watch the meeting instead of the screen.");
        ShortcutTable(
            ("Enter", "TAKE — crossfade PROGRAM to PREVIEW"),
            ("Space", "CUT — instant switch to PREVIEW"),
            ("F1 / F2 / F3", "Stage CAM / MEDIA / OTS in PREVIEW"),
            ("0", "Wide — reset PREVIEW to the full camera shot"),
            ("1 – 6", "Recall preset (fades in per AUTO-TAKE)"),
            ("Ctrl + 1 – 6", "Save current PREVIEW framing to that preset"),
            ("A", "Toggle AUTO-TAKE"),
            ("F4", "Toggle AUTO SCENES"),
            ("F11", "Toggle Compact layout"));

        Section("Settings, Explained");
        Body("Open Settings from the toolbar. It's a separate window you can leave open while you keep working — changes apply immediately.");
        Sub("Crossfade Duration");
        Body("How long, in milliseconds, a TAKE crossfade takes. Shorter feels snappier; longer feels gentler. 300ms (the default) is a natural, unhurried fade.");
        Sub("Over-the-Shoulder Box Size / Top Margin / Right Margin");
        Body("Controls the size and placement of the picture-in-picture box used by the OTS scene — how big it is, and how far from the top and right edges of the screen.");
        Sub("Hide Capture Border");
        Body("Asks Windows not to draw the yellow highlight border around whatever window or screen you're capturing for Media. Leave this on unless you have a reason to see that border yourself.");
        Sub("Shift Camera for OTS / OTS Shift Amount");
        Body("When on, the camera framing shifts slightly left while OTS is showing, so the picture box in the corner doesn't cover the subject. The shift amount slider controls how far.");
        Sub("Always Full Screen First");
        Body("When on, AUTO SCENES sends both videos and still pictures to full-screen MEDIA (instead of boxing stills into the smaller OTS corner). Turn this on if your hall prefers pictures and slides to always fill the whole screen first, with over-the-shoulder used only when you switch to it by hand (OTS / F3). This only affects automatic switching — you can always choose OTS manually regardless of this setting.");
        Sub("Check for Updates");
        Body("Checks whether a newer version of KH Video Switcher is available and, if so, lets you download and install it.");

        Section("Working with JW Library");
        Body("The app doesn't connect to JW Library directly — it simply watches (captures) the same screen or window JW Library is displayed on, the same way a camera would film a TV. From that, it can tell whether a video is currently playing, a still picture or song is showing, or the plain \"nothing playing\" screen is up.");
        Bullets(
            "If AUTO SCENES is on, the app switches your outgoing shot to match: a playing video brings up MEDIA, a still image brings up OTS or full-screen MEDIA (depending on the Always Full Screen First setting), and returning to the plain yeartext screen brings the camera (CAM) back automatically.",
            "You can always override by hand — pressing CAM, MEDIA, or OTS (or their F-keys) takes control immediately, whether or not AUTO SCENES is on.",
            "Songs are a special case worth knowing: JW Library shows the song lyrics as a picture, which the app can't tell apart from any other still image, so tap MEDIA (F2) yourself as the song starts. The app then holds that shot until the yeartext screen reappears, at which point it returns to camera automatically (this happens even with AUTO SCENES off, once you've taken MEDIA)."
        );
        Tip("Why the Set Stock step matters", "Every hall's yeartext / home screen looks a little different. Clicking STOCK teaches the app what your specific \"nothing playing\" screen looks like, so it isn't mistaken for a picture the first time AUTO SCENES runs.");

        Section("Sending Your Feed to Zoom");
        Body("The app doesn't publish directly to Zoom over the internet — instead it creates a \"virtual camera\" that Windows treats exactly like a plugged-in webcam, named \"KH Video Switcher\". Any app that lets you choose a camera, including Zoom, can then use it.");
        Steps(
            "In KH Video Switcher, click Virtual Camera (or the VIRTUAL CAM status tag) to turn it on.",
            "In Zoom, open your video/camera settings (the little arrow next to the camera icon, or Settings → Video).",
            "Choose \"KH Video Switcher\" from the camera list instead of your physical webcam.",
            "Whatever is currently on PROGRAM in KH Video Switcher is now exactly what your Zoom meeting sees.");
        Tip("If \"KH Video Switcher\" doesn't appear in Zoom's camera list", "The virtual camera has to be registered once for the whole computer, which happens automatically when the app is installed in \"all users\" mode with administrator approval. If it was installed \"just for me\" instead, ask whoever set up the computer to reinstall in all-users mode — everything else in the app still works either way, just not sending to Zoom.");

        Section("Example Scenarios");
        Sub("A public talk with a video illustration");
        Steps(
            "Start on CAM, framed on the speaker.",
            "When the speaker plays a video, either let AUTO SCENES switch to MEDIA automatically, or tap MEDIA (F2) yourself right as it starts.",
            "When the video ends and the speaker resumes, tap CAM (F1) — or let AUTO SCENES bring it back once the screen returns to yeartext.");
        Sub("A Watchtower Study or similar with pictures on screen");
        Steps(
            "Keep CAM on the conductor/reader as normal.",
            "When a picture comes up, AUTO SCENES will bring up OTS (camera plus a small picture box) by default — or full-screen MEDIA if you've turned on Always Full Screen First in Settings, for halls that prefer the picture to fill the screen.",
            "No action needed to return to CAM — it happens automatically once JW Library goes back to the yeartext screen between sections.");
        Sub("A song or sing-along");
        Steps(
            "As the song starts and lyrics appear, tap MEDIA (F2) — songs look like a still picture to the app, so this one is manual.",
            "The app holds that shot for the whole song.",
            "It returns to CAM by itself once the yeartext screen reappears after the song.");
        Sub("Switching between two speakers or a demonstration");
        Steps(
            "Before the meeting, frame each position (podium, demonstration table, second microphone) in PREVIEW and save each as its own preset.",
            "During the part, recall the next speaker's preset (number key) ahead of time to line it up in PREVIEW.",
            "Press TAKE right as they begin speaking for a smooth, professional-looking crossfade.");
        Sub("Getting the room ready before anyone arrives");
        Steps(
            "Open the app, Start the camera, Capture JW Library.",
            "Recall each saved preset in turn to confirm framing still looks right (camera position sometimes shifts slightly if it was bumped).",
            "Turn on Virtual Camera and confirm Zoom shows a live picture from \"KH Video Switcher\".",
            "Turn on AUTO SCENES if you plan to rely on it, and leave STOCK as previously set — no need to redo it unless JW Library's start screen has changed.");

        Section("Troubleshooting & Tips");
        Bullets(
            "STOCK tag shows a dashed outline / \"NOT SET\": click it while JW Library shows its plain yeartext screen — needed once per computer for AUTO SCENES to work reliably.",
            "AUTO SCENES doesn't seem to react: make sure Media Capture is running (the Capture button should say \"Captured\"), and that STOCK has been set.",
            "Nothing shows in PREVIEW or PROGRAM: make sure the camera has been Started and, for the media side, that Capture is running.",
            "Zoom doesn't list \"KH Video Switcher\" as a camera option: see \"Sending Your Feed to Zoom\" above — this usually means the app was installed without administrator rights, so the virtual camera was never registered.",
            "A picture looks off-center in OTS: adjust Shift Camera for OTS and the OTS Shift Amount slider in Settings so the camera framing leaves room for the picture box.",
            "Want pictures to always fill the whole screen instead of a small box: turn on Always Full Screen First in Settings.");
    }
}
