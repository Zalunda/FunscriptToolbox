-- FunscriptToolbox.MotionVectors.UI LUA Wrappper Version 1.0.0
json = require "json"
server_connection = require "server_connection"

-- global var
FTMVSFullPath = "C:\\Partage\\Medias\\Sources\\GitHub.Mine\\FunscriptToolbox\\src\\FunscriptToolbox\\bin\\Release\\FunscriptToolbox.exe"
status = "FunscriptToolbox.MotionVectors.UI not running"
updateCounter = 0
scriptIdx = 1

config = {
	enableLogs = false,
	learningZoneDurationInSeconds = 10,
	durationToGenerateInSeconds = 60,
	minimumActionDurationInMilliseconds = 170
}

connection = nil

function init()
    print("OFS Version:", ofs.Version())
	-- TODO cleanup old communicationChannel
	
	connection = server_connection:new(FTMVSFullPath, config.enableLogs)

    status = "FunscriptToolbox.MotionVectors.UI running"
end

function createRequest(service)

    local videoFullPath = player.CurrentVideo()
	local mvsFullPath = videoFullPath:gsub("-visual.mp4", "")
 	return {
		["$type"] = service,
		VideoFullPath = videoFullPath,
		MvsFullPath = mvsFullPath,
		CurrentVideoTime = math.floor(player.CurrentTime() * 1000)
	}
end

function binding.start_funscripttoolbox_motionvectors_ui()

	-- TODO Put stuff in config
 	local request = createRequest("ServerRequestCreateRulesFromScriptActions");
	request.Actions = {}
	request.DurationToGenerateInSeconds = config.durationToGenerateInSeconds
	request.MinimumActionDurationInMilliseconds = config.minimumActionDurationInMilliseconds
	request.ShowUI = false

	scriptIdx = ofs.ActiveIdx()
	script = ofs.Script(scriptIdx)
	local firstAction, indexBefore = script:closestActionAfter(player.CurrentTime() - config.learningZoneDurationInSeconds - 0.1)
	local lastAction, indexAfter = script:closestActionBefore(player.CurrentTime() + 0.1)
    for _, action in ipairs(script.actions) do
		if (action.at >= firstAction.at and action.at <= lastAction.at) then
			table.insert(request.Actions, { at = math.floor(action.at * 1000 + 0.5), pos = action.pos })
		end
	end

	connection:sendRequest(request, handleCreateRulesFromScriptActionsResponse)
end

function handleCreateRulesFromScriptActionsResponse(response)
	script = ofs.Script(scriptIdx)
	
    for i, action in ipairs(response.Actions) do
		local new_action = Action.new(action.at / 1000.0, action.pos, true)
		script.actions:add(new_action)
    end
	
	print('commiting...')
	script:commit()
	print('done.')
end

function update(delta)
    updateCounter = updateCounter + 1
    if math.fmod(updateCounter, 10) == 0 then
		connection:processResponses()
    end
end

function import_funscript_generator_json_result()
    local f = io.open(outputParametersFile)
    if not f then
        print('json parameters file not found')
        return
    end

    local content = f:read("*a")
    f:close()
	print(content)
    json_body = json.decode(content)
    actions = json_body.Actions
	
	script = ofs.Script(scriptIdx)
	
    for i, action in ipairs(actions) do
		local new_action = Action.new(action.at / 1000.0, action.pos, true)
		script.actions:add(new_action)
    end
	
	print('commiting...')
	script:commit()
	print('done.')
end

function is_empty(s)
  return s == nil or s == ''
end

-- function gui()
--     ofs.Text("Status: "..status)
--     ofs.Text("Version: "..mtfgVersion)
--     ofs.Text("Action:")
-- 
--     ofs.SameLine()
--     if not processHandleMTFG then
-- 
--         if ofs.Button("Start MTFG") then
--             binding.start_funscript_generator()
--         end
--     else
--         if ofs.Button("Kill MTFG") then
--             if platform == "Windows" then
--                 os.execute("taskkill /f /im funscript-editor.exe")
--             else
--                 os.execute("pkill -f funscript-editor.py")
--             end
--         end
--     end
-- 
--     ofs.SameLine()
--     if ofs.Button("Open Config") then
--         if platform == "Windows" then
--             processHandleConfigDir = Process.new("explorer.exe", ofs.ExtensionDir().."\\funscript-editor\\funscript_editor\\config")
--         else
--             local cmd = '/usr/bin/dbus-send --session --print-reply --dest=org.freedesktop.FileManager1 --type=method_call /org/freedesktop/FileManager1 org.freedesktop.FileManager1.ShowItems array:string:"file://'
--                 ..ofs.ExtensionDir()..'/Python-Funscript-Editor/funscript_editor/config/" string:""'
--             -- print("cmd: ", cmd)
--             os.execute(cmd)
--         end
--     end
-- 
--     if logfileExist then
--         if platform == "Windows" then
--             ofs.SameLine()
--             if ofs.Button("Open Log") then
--                  processHandleLogFile = Process.new("notepad.exe", "C:/Temp/funscript_editor.log")
--             end
--         else
--             ofs.SameLine()
--             if ofs.Button("Open Log") then
--                 processHandleLogFile = Process.new("/usr/bin/xdg-open", "/tmp/funscript_editor.log")
--             end
--         end
--     end
-- 
--     if tmpFileExists then
--         ofs.SameLine()
--         if ofs.Button("Force Import") then
--             scriptIdx = ofs.ActiveIdx()
--             import_funscript_generator_json_result()
--         end
--     end
-- 
--     ofs.Separator()
--     ofs.Text("Options:")
--     stopAtNextActionPoint, _ = ofs.Checkbox("Stop tracking at next existing point", stopAtNextActionPoint)
--     enableLogs, _ = ofs.Checkbox("Enable logging", enableLogs)
--     multiaxis, _ = ofs.Checkbox("Enable multiaxis", multiaxis)
-- 
--     if multiaxis then
--         local comboNum = 1
--         for k,v in pairs(scriptAssignment) do
--             ofs.Text("  o "..k.." ->")
--             ofs.SameLine()
--             if v.idx > scriptNamesCount then
--                 v.idx = 1
--             end
--             v.idx, _ = ofs.Combo("#"..tostring(comboNum), v.idx, scriptNames)
--             comboNum = comboNum + 1
--         end
--     end
-- 
--     ofs.Separator()
-- 
--     local enable_post_processing = true
--     if enable_post_processing then
--         ofs.Text("Post-Processing:")
--         ofs.SameLine()
--         if ofs.Button("Invert") then
--             invert_selected()
--         end
-- 
--         ofs.SameLine()
--         if ofs.Button("Align Bottom Points") then
--             align_bottom_points(-1)
--         end
-- 
--         ofs.SameLine()
--         if ofs.Button("Align Top Points") then
--              align_top_points(-1)
--         end
--     end
-- 
-- end
