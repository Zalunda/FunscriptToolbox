-- FunscriptToolbox.MotionVectors LUA Wrappper Version 1.0.0
json = require "json"
server_connection = require "server_connection"
virtual_actions = require "virtual_actions"

-- global var
FTMVSFullPath = "C:\\Partage\\Medias\\Sources\\GitHub.Mine\\FunscriptToolbox\\src\\FunscriptToolbox\\bin\\Release\\FunscriptToolbox.exe" --TODO Replace with  [[FunscriptToolboxExePathInLuaFormat]]
status = "FunscriptToolbox.MotionVectors not running"
updateCounter = 0
scriptIdx = 1

config = {
	PluginVersion = "1.0.0", -- TODO Replace with "[[PluginVersion]]",
	EnableLogs = false,
	TopPointsOffset = 0,
	BottomPointsOffset = 0,
	MinimumPosition = 0,
	MaximumPosition = 100,
	MinimumPercentageFilled = 0,
	AmplitudeCenter = 50,
	ExtraAmplitudePercentage = 0
}

sharedConfig = {
	MaximumMemoryUsageInMB = 1000,
	LearningDurationInSeconds = 10,
	DefaultActivityFilter = 60,
	DefaultQualityFilter = 75,
	MaximumDurationToGenerateInSeconds = 60,
	MaximumNbStrokesDetectedPerSecond = 3.0
}

connection = nil

function init()
    print("Plugin Version:", config.PluginVersion)
	-- TODO cleanup old communicationChannel
	
	connection = server_connection:new(FTMVSFullPath, config.enableLogs)
	virtualActions = {}
	
	sendCheckVersionRequest()

    status = "FunscriptToolbox.MotionVectors running"
end

function update(delta)
    updateCounter = updateCounter + 1
    if math.fmod(updateCounter, 10) == 0 then
		connection:processResponses()
		getVirtualActions():removeActionsBefore(player.CurrentTime())
    end
end

function getVirtualActions(scriptIdx)
	if not scriptIdx then
		scriptIdx = ofs.ActiveIdx()
	end
	
	local va = virtualActions[scriptIdx]
	if not va then
		va = virtual_actions:new(scriptIdx, config)
		virtualActions[scriptIdx] = va
	end
	return va
end

function createRequest(service)

    local videoFullPath = player.CurrentVideo()
	local mvsFullPath = videoFullPath:gsub("-visual.mp4", "") -- TODO Better way to find .mvs
 	return {
		["$type"] = service,
		VideoFullPath = videoFullPath,
		CurrentVideoTime = math.floor(player.CurrentTime() * 1000),
		MvsFullPath = mvsFullPath,
		SharedConfig = sharedConfig
	}
end

function sendCreateRulesRequest(showUI)
	getVirtualActions():removeVirtualActionsInTimelime()

	scriptIdx = ofs.ActiveIdx()
	script = ofs.Script(scriptIdx)
	local firstAction, indexBefore = script:closestActionAfter(player.CurrentTime() - sharedConfig.LearningDurationInSeconds)
	local lastAction, indexAfter = script:closestActionBefore(player.CurrentTime() + 0.001)
	local nextAction = script:closestActionAfter(player.CurrentTime() + 0.001);

 	local request = createRequest("CreateRulesPluginRequest");
	request.Actions = {}
	request.SelectedActions = {}

	if nextAction then
		request.DurationToGenerateInSeconds = math.min(sharedConfig.MaximumDurationToGenerateInSeconds, nextAction.at - player.CurrentTime())
	else
		request.DurationToGenerateInSeconds = sharedConfig.MaximumDurationToGenerateInSeconds
	end
	request.ShowUI = showUI

    for _, action in ipairs(script.actions) do
		if (action.at >= firstAction.at and action.at <= lastAction.at) then
			table.insert(request.Actions, { at = math.floor(action.at * 1000 + 0.5), pos = action.pos })
		end
	end
	
	if script:hasSelection() then
		for _, indice in ipairs(script:selectedIndices()) do
			print(indice)
		
		    local action = script.actions[indice]
			table.insert(request.SelectedActions, { at = math.floor(action.at * 1000 + 0.5), pos = action.pos })
		end
	end

	connection:sendRequest(request, handleCreateRulesResponse)
end

function handleCreateRulesResponse(response)
	if response.Actions then
		scriptIdx = ofs.ActiveIdx() -- todo save in request/response
		local va = getVirtualActions(scriptIdx)
		va:init(response.Actions, response.FrameDurationInMs)
	end
end

function sendCheckVersionRequest()

 	local request = createRequest("CheckVersionPluginRequest");
	request.PluginVersion = config.PluginVersion
	connection:sendRequest(request, handleCheckVersionResponse)
end

function handleCheckVersionResponse(response)
	if config.PluginVersion == response.LastestVersion then
		print('Plugin is up to date.')
	else
		print('A new version of the plugin is available. Please rerun "FunscriptToolbox installation" script')
	end
end

--------------------------------------------------------------------
-- BINDINGS
--------------------------------------------------------------------
function binding.start_funscripttoolbox_motionvectors_without_ui()
	sendCreateRulesRequest(false)
end
function binding.start_funscripttoolbox_motionvectors_with_ui()
	sendCreateRulesRequest(true)
end
function binding.top_points_move_to_left()
	config.TopPointsOffset = config.TopPointsOffset - 5
	updateVirtualPoints()
end 
function binding.top_points_move_to_right()
	config.TopPointsOffset = config.TopPointsOffset + 5
	updateVirtualPoints()
end 
function binding.bottom_points_move_to_left()
	config.BottomPointsOffset = config.BottomPointsOffset - 5
	updateVirtualPoints()
end 
function binding.min_position_move_up()
	config.MinimumPosition = config.MinimumPosition + 5
	updateVirtualPoints()
end 
function binding.min_position_move_down()
	config.MinimumPosition = config.MinimumPosition - 5
	updateVirtualPoints()
end 
function binding.max_position_move_up()
	config.MaximumPosition = config.MinimumPosition + 5
	updateVirtualPoints()
end 
function binding.max_position_move_down()
	config.MaximumPosition = config.MinimumPosition - 5
	updateVirtualPoints()
end 
function binding.min_percentage_filled_move_up()
	config.MinimumPercentageFilled = config.MinimumPercentageFilled + 10
	updateVirtualPoints()
end 
function binding.min_percentage_filled_move_down()
	config.MinimumPercentageFilled = config.MinimumPercentageFilled - 10
	updateVirtualPoints()
end 
function binding.center_move_up()
	config.AmplitudeCenter = config.AmplitudeCenter + 10
	updateVirtualPoints()
end 
function binding.center_move_down()
	config.AmplitudeCenter = config.AmplitudeCenter - 10
	updateVirtualPoints()
end 
function binding.center_top()
	config.AmplitudeCenter = 100
	updateVirtualPoints()
end 
function binding.center_bottom()
	config.AmplitudeCenter = 0
	updateVirtualPoints()
end 
function updateVirtualPoints()
	config.MinimumPosition = clamp(config.MinimumPosition, 0, 95)
	config.MaximumPosition = clamp(config.MaximumPosition, config.MinimumPosition + 5, 100)
	config.MinimumPercentageFilled = clamp(config.MinimumPercentageFilled, 0, 100)
	config.AmplitudeCenter = clamp(config.AmplitudeCenter, 0, 100)
	getVirtualActions():update()
end

--------------------------------------------------------------------
-- GUI
--------------------------------------------------------------------
function gui()
    ofs.Text("Connection: " .. connection:getStatus())
    ofs.Text("Virtual Actions: " .. getVirtualActions():getStatus())
	ofs.Separator()
	
	if ofs.CollapsingHeader("Virtual Actions") then
		config.TopPointsOffset, topChanged = ofs.InputInt("Top Points Offset", config.TopPointsOffset, 1)
		config.BottomPointsOffset, bottomChanged = ofs.InputInt("Bottom Points Offset", config.BottomPointsOffset, 1)
		config.MinimumPosition, minChanged = ofs.InputInt("Min Pos", config.MinimumPosition, 5)
		config.MaximumPosition, maxChanged = ofs.InputInt("Max Pos", config.MaximumPosition, 5)	
		config.AmplitudeCenter, amplitudeChanged = ofs.InputInt("Center Pos %", config.AmplitudeCenter, 10)
		config.MinimumPercentageFilled, percentageChanged = ofs.InputInt("Min Pos % filled", config.MinimumPercentageFilled, 10)
		ofs.SameLine()
		if ofs.Button("Bottom") then
			config.AmplitudeCenter = 0
			amplitudeChanged = true
		end
		ofs.SameLine()
		if ofs.Button("Top") then
			config.AmplitudeCenter = 100
			amplitudeChanged = true
		end
		config.ExtraAmplitudePercentage, extraAmplitudeChanged = ofs.InputInt("Extra %", config.ExtraAmplitudePercentage, 10)		
		config.ExtraAmplitudePercentage = clamp(config.ExtraAmplitudePercentage, 0, 1000)
		ofs.SameLine()
		if ofs.Button("Reset") then
			config.ExtraAmplitudePercentage = 0
			extraAmplitudeChanged = true
		end
		
		if ofs.Button("Hide") then
			getVirtualActions():removeVirtualActionsInTimelime()
		end
		ofs.SameLine()
		if ofs.Button("Show") then
			updateVirtualPoints()
		end
		ofs.SameLine()
		if ofs.Button("Delete") then
			getVirtualActions():deleteVirtualActions()
		end
		if ofs.Button("Reset Settings") then
			config.TopPointsOffset = 0
			config.BottomPointsOffset = 0
			config.MinimumPosition = 10
			config.MaximumPosition = 90
			config.AmplitudeCenter = 50
			config.MinimumPercentageFilled = 60
			amplitudeChanged = true
		end

		if topChanged or bottomChanged or minChanged or maxChanged or percentageChanged or amplitudeChanged or extraAmplitudeChanged then
			updateVirtualPoints()
		end

		-- TODO Save Preset
	end	

	if ofs.CollapsingHeader("Learn from script") then
		sharedConfig.LearningDurationInSeconds, _ = ofs.InputInt("duration (sec)", sharedConfig.DefaultLearningDurationInSeconds, 2)
		sharedConfig.LearningDurationInSeconds = clamp(sharedConfig.DefaultLearningDurationInSeconds, 0, sharedConfig.MaximumLearningDurationInSeconds)	
		sharedConfig.DefaultActivityFilter, _ = ofs.InputInt("Default Activity Filter", sharedConfig.DefaultActivityFilter, 5)
		sharedConfig.DefaultActivityFilter = clamp(sharedConfig.DefaultActivityFilter, 0, 100)
		sharedConfig.DefaultQualityFilter, _ = ofs.InputInt("Default Quality Filter", sharedConfig.DefaultQualityFilter, 5)
		sharedConfig.DefaultQualityFilter = clamp(sharedConfig.DefaultQualityFilter, 50, 100)
	end	
	if ofs.CollapsingHeader("Actions generation") then
		sharedConfig.MaximumDurationToGenerateInSeconds, _ = ofs.InputInt("Maximum Generation (sec)", sharedConfig.MaximumDurationToGenerateInSeconds, 10)
		sharedConfig.MaximumDurationToGenerateInSeconds = clamp(sharedConfig.MaximumDurationToGenerateInSeconds, 20, 100000)
	
		sharedConfig.MaximumNbStrokesDetectedPerSecond, _ = ofs.Input("Maximum Strokes per sec", sharedConfig.MaximumNbStrokesDetectedPerSecond, 0.5)
		sharedConfig.MaximumNbStrokesDetectedPerSecond = clamp(sharedConfig.MaximumNbStrokesDetectedPerSecond, 1.0, 5.0)
	end
	if ofs.CollapsingHeader("Others config") then
		sharedConfig.MaximumMemoryUsageInMB, _ = ofs.InputInt("Maximum Memory Usage (MB)", sharedConfig.MaximumMemoryUsageInMB, 50)
		sharedConfig.MaximumMemoryUsageInMB = clamp(sharedConfig.MaximumMemoryUsageInMB, 0, 100000)
	end
end
