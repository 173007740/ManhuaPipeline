UPDATE SeedancePrompts
SET Duration = 11,
    PromptTextH3 = N'【参考素材说明】
全能参考生视频模式（共 4 个输入素材，全部按上传顺序编号并指派用途）：
@图片1（[陈默]）：是人物参考，本镜头中不出画，仅作为画外视线主体，不出现任何人物。
@图片2（[老板]）：是人物参考，本镜头中不出画，仅作前序剧情上下文。
@图片3（[老板办公室]）：是环境参考，仅用于开场窗前取景：窗框、桌面一角与主机等室内元素需参考该图。
@图片4（窗外灰蒙蒙阴天与楼宇剪影）：是天空/画面风格参考。开场的灰蒙阴天、低垂云层与远处楼宇剪影按该图锚定，后续随风暴演变为乌云翻滚、闪电与暴雨。

subject_definitions:
<Subject 1> is the middle-aged programmer Chen Mo (陈默) in <Picture 1>, with a weary, unshaven face, hollow eyes, and a slightly hunched posture. He does not appear in this video; the shot is from his seated eye level looking out the window, his presence conveyed only through the unblinking fixed perspective at the start.
<Subject 2> is the company boss in <Picture 2>, with a cold, businesslike expression, neat but plain attire, and a steady, emotionless gaze. He does not appear in this video.
<Subject 3> is the boss''s office in <Picture 3>, with a wide desk, a computer on the side, floor-to-ceiling windows at the back, and a cold, clean interior. Only the window frame, a desk corner and the computer tower appear along the frame edges in the opening.
<Subject 4> is the gloomy overcast sky outside the window in <Picture 4>, with low-hanging clouds and blurred, distant building silhouettes. It anchors the opening mood and the spatial direction of the window; the sky then evolves into dark roiling storm clouds, lightning and heavy rain.

summary:
[reference generation] The target video is an 11-second emotional escalation shot from Chen Mo''s point of view after a cold judgment: he looks out of the office window at a depressing overcast sky; the camera slowly pushes in, passes out through the window into the air and rises toward the sky; the gray clouds darken and churn as the camera climbs among them; a jagged lightning bolt splits the clouds with a blinding white flash and a thunderclap booms; then heavy rain pours down from the dark sky, the shot holding on the downpour as the emotional release lands before fading out. No characters appear in frame; no subtitles, no watermarks.

retention_analysis:
<Subject 1> (appears across the whole shot as off-screen eye): attribute_transfer - the viewing angle originates from Chen Mo''s seated position at the start, his presence conveyed only through the fixed perspective that then lifts off and leaves the room behind.
<Subject 2> (does not appear in this shot): weak_reference - used solely as context for the preceding scene, not visible in the frame.
<Subject 3> (appears in [Shot 1]): partially_preserved - the window frame, a desk corner and the computer tower appear along the frame edges at the opening, then fall away as the camera passes through the window.
<Subject 4> (appears in [Shot 1] through [Shot 5]): fully_preserved - the opening gray overcast sky and building silhouettes follow the reference, then develop continuously into dark churning clouds, a jagged lightning flash and heavy rain in the same cold blue-gray key.

detailed_description:
The target video is in a realistic cinematic, live-action style with a low-saturation cold gray-blue color palette, no subtitles, no watermarks, no character-name overlays. No human figure appears in any shot; the frame is occupied by the window view, the sky and the storm.
[Shot 1] At 00:00.000-00:02.500, a fixed symmetrical composition frames the view through a large office window, seen from Chen Mo''s seated eye level. On the left and right edges, the window frame and a small corner of the desk with a humming computer tower are faintly visible. The entire window is filled with a gray overcast sky; distant building silhouettes sit blurred near the low horizon. Not a single ray of sunlight breaks through. The camera is still at first, then begins a slow, steady push toward the window. A faint, steady hum of a computer fan is the only sound, emphasizing the crushing silence. <Subject 3> provides the indoor frame, <Subject 4> fills the background.
[Shot 2] At 00:02.500-00:05.000, the camera continues pushing in and passes out through the window into the open air, leaving the office behind; the window frame and desk slip below and out of frame as the viewpoint rises into the sky, the blurred building silhouettes dropping away beneath. The gray sky fills the frame, flat and diffused, the atmosphere pressing down. The computer fan hum fades out and is replaced by rising wind.
[Shot 3] At 00:05.000-00:07.000, the camera climbs into the cloud layer as the gray clouds darken into heavy, churning storm clouds, their mass rolling and swelling, the light turning murky and low; faint electrical flickers glimmer briefly inside the cloud depths, building tension. Wind noise rises.
[Shot 4] At 00:07.000-00:09.000, a jagged lightning bolt splits the dark clouds with a blinding white flash that floods the frame, immediately followed by a deep, rolling thunderclap that rumbles through the sky; the clouds flicker with the residual glow of the discharge, the storm now fully unleashed.
[Shot 5] At 00:09.000-00:11.000, heavy rain pours down from the dark sky, dense sheets of rain streaking past the camera, the clouds still churning overhead with occasional distant flashes; the shot holds on the downpour as the emotional release lands, then the frame slowly fades to black by 00:11.000.

overall_soundscape:
A low computer fan hum fading out, rising wind as the camera leaves the window, then a deep thunderclap and sustained heavy rain, all gradually fading into silence.

non_diegetic_music:
N/A

'
WHERE PromptId = 4624;
