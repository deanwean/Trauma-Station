// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from Goob Station's voice chat implementation.
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;

namespace Content.Goobstation.Client.VoiceChat.UI;

public sealed class VoiceSpeakerList : Control
{
    private const int MaxEntries = 8;
    private const float MinVisibility = 0.01f;

    private readonly BoxContainer _entries;
    private readonly Dictionary<NetEntity, VoiceSpeakerEntry> _bySpeaker = new();
    private readonly List<VoiceSpeakerEntry> _expired = new();
    private VoiceSpeakerEntry? _self;

    public VoiceSpeakerList()
    {
        _entries = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 2,
            HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 110),
        };
        AddChild(_entries);
    }

    public void SetInset(float right, float bottom)
    {
        var margin = new Thickness(0, 0, right, bottom);
        if (_entries.Margin != margin)
            _entries.Margin = margin;
    }

    public void Update(float frameTime, ProximityVoiceSystem voice)
    {
        var added = false;
        foreach (var (id, stream) in voice.Streams)
        {
            if (_bySpeaker.Count >= MaxEntries)
                break;

            if (_bySpeaker.ContainsKey(id) ||
                voice.IsSelf(id) ||
                !stream.Playing ||
                stream.Audibility < MinVisibility)
            {
                continue;
            }

            var entry = new VoiceSpeakerEntry(id);
            _bySpeaker[id] = entry;
            entry.MouseFilter = MouseFilterMode.Stop;
            entry.OnKeyBindDown += args =>
            {
                if (args.Function != EngineKeyFunctions.UIRightClick)
                    return;
                voice.OpenSpeakerVolume(id, entry.DisplayName);
                args.Handle();
            };
            _entries.AddChild(entry);
            added = true;
        }

        foreach (var entry in _bySpeaker.Values)
        {
            if (voice.Streams.TryGetValue(entry.Speaker, out var stream))
            {
                var visibility = stream.Audibility;
                var active = stream.Activity > 0f && visibility >= MinVisibility;
                var level = stream.Levels.Overall * (0.25f + 0.75f * visibility);
                entry.Update(frameTime, active, level, voice.DescribeSpeaker(stream),
                    voice.GetRouteLabel(stream), voice.GetRouteColor(stream));
            }
            else
            {
                entry.Update(frameTime, false, 0f, entry.DisplayName, null, Color.White);
            }

            if (entry.Expired)
                _expired.Add(entry);
        }

        foreach (var entry in _expired)
        {
            _bySpeaker.Remove(entry.Speaker);
            entry.Orphan();
        }

        _expired.Clear();
        UpdateSelf(frameTime, voice, added);
    }

    public void Clear()
    {
        _bySpeaker.Clear();
        _self = null;
        _entries.RemoveAllChildren();
    }

    private void UpdateSelf(float frameTime, ProximityVoiceSystem voice, bool reorder)
    {
        var self = voice.SelfActivity;
        if (_self == null)
        {
            if (self <= 0f)
                return;

            _self = new VoiceSpeakerEntry(NetEntity.Invalid);
            _entries.AddChild(_self);
        }
        else if (reorder)
        {
            _self.SetPositionLast();
        }

        _self.Update(
            frameTime,
            self > 0f,
            voice.SelfLevels.Overall,
            Loc.GetString("voice-speaker-self"),
            voice.GetSelfLabel(),
            voice.GetSelfColor());

        if (!_self.Expired)
            return;

        _self.Orphan();
        _self = null;
    }
}

public sealed class VoiceSpeakerEntry : Control
{
    private const int HistoryLength = 64;
    private const float SampleInterval = 0.05f;
    private const float HoldTime = 1.2f;
    private const float FadeTime = 0.4f;
    private const float MinVisibility = 0.01f;

    private static readonly Color Background = Color.Black.WithAlpha(0.55f);

    private readonly float[] _history = new float[HistoryLength];
    private readonly Label _name;
    private readonly Label _route;
    private int _head;
    private int _count;
    private float _sinceSample;
    private float _level;
    private float _silentFor;
    private Color _color = Color.White;

    public readonly NetEntity Speaker;

    public string DisplayName => _name.Text ?? string.Empty;

    public bool Expired => _silentFor >= HoldTime + FadeTime;

    public VoiceSpeakerEntry(NetEntity speaker)
    {
        Speaker = speaker;
        MinSize = new Vector2(200, 22);
        RectClipContent = true;

        _name = new Label
        {
            HorizontalExpand = true,
            ClipText = true,
            VerticalAlignment = VAlignment.Center,
        };
        _route = new Label
        {
            VerticalAlignment = VAlignment.Center,
            Visible = false,
        };

        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 6,
            Margin = new Thickness(8, 0, 6, 0),
        };
        row.AddChild(_name);
        row.AddChild(_route);
        AddChild(row);
    }

    public void Update(float frameTime, bool active, float level, string name, string? route, Color color)
    {
        _level = active ? level : 0f;
        _silentFor = active ? 0f : _silentFor + frameTime;
        _color = color;

        if (_name.Text != name)
            _name.Text = name;

        _route.Visible = route != null;
        if (route != null && _route.Text != route)
            _route.Text = route;
        _route.FontColorOverride = color;

        _sinceSample = MathF.Min(_sinceSample + frameTime, SampleInterval * HistoryLength);
        while (_sinceSample >= SampleInterval)
        {
            _sinceSample -= SampleInterval;
            _history[_head] = _level;
            _head = (_head + 1) % HistoryLength;
            _count = Math.Min(_count + 1, HistoryLength);
        }

        var fade = _silentFor <= HoldTime ? 1f : 1f - (_silentFor - HoldTime) / FadeTime;
        Modulate = Color.White.WithAlpha(Math.Clamp(fade, 0f, 1f));
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var width = (float) PixelWidth;
        var height = (float) PixelHeight;
        handle.DrawRect(new UIBox2(0f, 0f, width, height), Background);

        var fill = _color.WithAlpha(0.3f);
        var barWidth = width / (HistoryLength - 1);
        var scroll = _sinceSample / SampleInterval * barWidth;

        DrawColumn(handle, width - scroll, width, _level, height, fill);
        for (var i = 0; i < _count; i++)
        {
            var right = width - scroll - i * barWidth;
            if (right <= 0f)
                break;

            var level = _history[(_head - 1 - i + HistoryLength) % HistoryLength];
            DrawColumn(handle, MathF.Max(0f, right - barWidth), right, level, height, fill);
        }

        handle.DrawRect(new UIBox2(0f, 0f, MathF.Max(2f, 3f * UIScale), height), _color);
    }

    private static void DrawColumn(DrawingHandleScreen handle, float left, float right, float level, float height, Color color)
    {
        if (level <= MinVisibility || right <= left)
            return;

        var half = level * (height - 4f) * 0.5f;
        var center = height * 0.5f;
        handle.DrawRect(new UIBox2(left, center - half, right, center + half), color);
    }
}
