fullstroke = require "fullstroke"

-- FunscriptToolbox.MotionVectors LUA virtual_actions
local virtual_actions = {}

function virtual_actions:new(scriptIdx, config)
	local o = { 
		ScriptIdx = scriptIdx,		
		Config = config,
		FrameDurationInSec = nil,
		GeneratedActions = nil,
		GeneratedFullStrokes = nil
	}
	setmetatable(o, {__index = virtual_actions})
	return o
end

function virtual_actions:init(userAction, generatedActions, frameDurationInSec)
	self:removeAllVirtualActionsInTimelime(userAction .. ' init')
	self.GeneratedActions = {}
	if #generatedActions > 0 then	
		self:updateDebugScriptIfNeeded(userAction, generatedActions)

		for i, action in ipairs(generatedActions) do	
			local new_action = { 
				originalAt = action.at / 1000,
				originalPos = action.pos,
				isTop = action.pos ~= 0,
				at = action.at / 1000,
				atMin = action.AtMin / 1000,
				atMax = action.AtMax / 1000,
				pos = action.pos,
				enabled = true
			}
			table.insert(self.GeneratedActions, new_action)
		end

		-- Try to 'attach' the first and last generated actions to existing actions point
		local script = ofs.Script(self.ScriptIdx)
		local firstGeneratedAction = self.GeneratedActions[1]
		local firstActionMatched = script:closestAction(firstGeneratedAction.at)
		if firstActionMatched and math.abs(firstGeneratedAction.at - firstActionMatched.at) < frameDurationInSec then
			printWithTime(userAction, 'locking first point to existing point', getFormattedTime(firstActionMatched.at))
			firstGeneratedAction.at = firstActionMatched.at
			firstGeneratedAction.originalAt = firstActionMatched.at
			firstGeneratedAction.pos = firstActionMatched.pos
			firstGeneratedAction.originalPos = firstActionMatched.pos
			firstGeneratedAction.enabled = false
		end

		local lastGeneratedAction = self.GeneratedActions[#self.GeneratedActions]
		local lastActionMatched = script:closestAction(lastGeneratedAction.at)
		if lastActionMatched and math.abs(lastGeneratedAction.at - lastActionMatched.at) < frameDurationInSec then
			printWithTime(userAction, 'locking last point to existing point', getFormattedTime(lastActionMatched.at))
			lastGeneratedAction.at = lastActionMatched.at
			lastGeneratedAction.originalAt = lastActionMatched.at
			lastGeneratedAction.pos = lastActionMatched.pos
			lastGeneratedAction.originalPos = lastActionMatched.pos
			lastGeneratedAction.enabled = false
		end

        -- Create an array of FullStrokes from GeneratedActions
		local actionsNotInStrokes
        self.GeneratedFullStrokes, actionsNotInStrokes = fullstroke.identifyFullStrokes(self.GeneratedActions)

        if config.EnableLogs then
            printWithTime(userAction, 'init.insert', 
						  #self.GeneratedActions, 'actions,', 
                          #self.GeneratedFullStrokes, 'strokes,', 
                          actionsNotInStrokes, 'points not in strokes')
        end

		self.FrameDurationInSec = frameDurationInSec
		self:update(userAction .. ' init')
	end
end

function virtual_actions:getFirstLastAndNbActions()
	local firstAction = nil
	local lastAction = nil
	local nbActions = 0
	for i, generatedAction in ipairs(self.GeneratedActions) do
		if generatedAction.enabled then
			if firstAction == nil then
				firstAction = generatedAction
			end
			lastAction = generatedAction
			nbActions = nbActions + 1
		end
	end

	return firstAction, lastAction, nbActions
end

function virtual_actions:removeAllVirtualActionsInTimelime(userAction)

	local script = ofs.Script(self.ScriptIdx)
	if self.GeneratedActions and #self.GeneratedActions > 0 then

	    local firstAction, lastAction, nbActions = self:getFirstLastAndNbActions()
	
		local logsInfo = ''
		for idx, action in ipairs(script.actions) do
			if action.at >= firstAction.at - self.FrameDurationInSec / 2 and action.at <= lastAction.at + self.FrameDurationInSec / 2 then
				script:markForRemoval(idx)
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
			end
		end
		script:removeMarked()
		script:commit()
		
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'removeAllVirtualActionsInTimelime', logsInfo) end
	end
end

function virtual_actions:unvirtualizeActionsBefore(userAction, time)
	
 	if self.GeneratedActions then
		local logsInfo = ''
		for i, generatedAction in ipairs(self.GeneratedActions) do
			if generatedAction.at <= time and generatedAction.enabled then
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(generatedAction.at) .. ', ' end
				generatedAction.enabled = false
			end
		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'unvirtualizeActionsBefore.GeneratedActions', #self.GeneratedActions, logsInfo) end
 	end
end

function virtual_actions:deleteAllVirtualActions(userAction)

	if config.EnableLogs then printWithTime(userAction, 'deleteAllVirtualActions') end
	self:removeAllVirtualActionsInTimelime(userAction .. ' deleteAllVirtualActions')
	self.GeneratedActions = {}
end

function virtual_actions:getStartTime()
	if self.GeneratedActions and #self.generatedAction > 0 then
		local firstAction, lastAction, nbActions = self:getFirstLastAndNbActions()
		return firstAction.at
	else
		return nil
	end
end

function virtual_actions:update(userAction)

	self:removeAllVirtualActionsInTimelime(userAction .. ' update')

	if self.GeneratedActions and #self.GeneratedActions > 0 then

		local script = ofs.Script(self.ScriptIdx)

		for i, action in ipairs(self.GeneratedActions) do
			action.at = action.originalAt + 0.001
			action.pos = action.originalPos
		end

		fullstroke.optimizeActionPointTimings(self.GeneratedFullStrokes, 0.2)

		fullstroke.writeDebugTimingFile(self.GeneratedFullStrokes, "debugFSTB.FullStrokes.log")

		-- Shift all point to the right/left

		-- 1. Add the points, ajusting the at with top/bottom offset
		-- local zoneStartAction = nil
		-- local zoneEndAction = nil
		-- for i, action in ipairs(self.GeneratedActions) do	
		-- 	if not action.locked then 
		-- 		if not zoneStartAction then
		-- 			zoneStartAction = script:closestAction(action.at)
		-- 			zoneEndAction = script:closestActionAfter(action.at + self.FrameDurationInSec / 2)
		-- 		end
		-- 	
		-- 		local newAt = action.at
		-- 		if action.isTop then
		-- 			newAt = action.at + self.FrameDurationInSec * self.Config.TopPointsOffset
		-- 		else
		-- 			newAt = action.at + self.FrameDurationInSec * self.Config.BottomPointsOffset
		-- 		end
		-- 		
		-- 		local new_action = Action.new(newAt, action.pos, false)
		-- 		table.insert(self.ActionsInTimeline, new_action)
		-- 	end
		-- end	
		-- local zoneStart = zoneStartAction and zoneStartAction.at or 0
		-- local zoneEnd = zoneEndAction and zoneEndAction.at or 10000000
		-- if config.EnableLogs then printWithTime(userAction, 'update', 'zoneStart', getFormattedTime(zoneStart)) end
		-- if config.EnableLogs then printWithTime(userAction, 'update', 'zoneEnd', getFormattedTime(zoneEnd)) end

		-- 2. Make sure that actions are still in order and don't overlap (because of the top/bottom offset).
		-- for i = 2, #self.ActionsInTimeline do
		-- 	local indexToFix = i
		-- 	while indexToFix > 1 and self.ActionsInTimeline[indexToFix - 1].at >= self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec / 2 do
		-- 		if config.EnableLogs then printWithTime(userAction, 'update', 'fixing', self.ActionsInTimeline[indexToFix - 1].at, 'to', self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec) end
		-- 		self.ActionsInTimeline[indexToFix - 1].at = self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec
		-- 		indexToFix = indexToFix - 1
		-- 	end
		-- end
			
		-- Adjust position, according to the user preference
		local totalAmplitude = self.Config.MaximumPosition - self.Config.MinimumPosition
		local ratio = totalAmplitude / 100
		local minAmplitude = self.Config.MinimumPercentageFilled * ratio		
		local amplitudeRange = (100 - self.Config.MinimumPercentageFilled) * ratio

		local previousPos = self.GeneratedActions[1].pos
		for i = 2, #self.GeneratedActions do
		    if self.GeneratedActions[i].enabled then
				local currentPos = self.GeneratedActions[i].pos;
			
				local distance = math.abs(previousPos - currentPos)
				if distance > 0 then
					local finalAmplitude = (minAmplitude + distance * amplitudeRange / 100) * (1 + self.Config.ExtraAmplitudePercentage / 100)
					local unusedAmplitude = totalAmplitude - finalAmplitude
					minPosition = self.Config.MinimumPosition
					maxPosition = self.Config.MaximumPosition
					if unusedAmplitude > 0 then									
						minPosition = minPosition + unusedAmplitude * self.Config.AmplitudeCenter / 100
						maxPosition = minPosition + finalAmplitude
					end
				
					if previousPos < currentPos then
				 		self.GeneratedActions[i - 1].pos = minPosition
				 		self.GeneratedActions[i].pos = maxPosition
					else
				 		self.GeneratedActions[i - 1].pos = maxPosition
				 		self.GeneratedActions[i].pos = minPosition
					end
					previousPos = currentPos
				end
			end
		end
		
		-- Remove the item that would be outside our 'zone' (i.e. they might overlap existing actions)
		-- local logsInfo = ''
		-- for i = #self.GeneratedActions, 1, -1 do
		-- 	local action = self.GeneratedActions[i]
		-- 	local closestAction = script:closestAction(action.at)
		-- 	if action.at <= zoneStart or action.at >= zoneEnd then
		-- 		if config.EnableLogs then logsInfo = logsInfo .. '[SKIP-zone] ' .. getFormattedTime(action.at) .. ', ' end
		-- 		table.remove(self.GeneratedActions, i)
		-- 	elseif closestAction and math.abs(closestAction.at - action.at) < self.FrameDurationInSec / 2 then
		-- 		if config.EnableLogs then logsInfo = logsInfo .. '[SKIP-too-close] ' .. getFormattedTime(action.at) .. ', ' end
		-- 	    table.remove(self.GeneratedActions, i)
		-- 	else
		-- 		if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
		-- 	end
		-- end
		-- if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'update', #self.GeneratedActions, logsInfo) end

		-- Finally, add the points to OFS timeline
		for i, generatedAction in ipairs(self.GeneratedActions) do
			if generatedAction.enabled then
				script.actions:add(Action.new(generatedAction.at, generatedAction.pos, false))
			end
		end

		script:commit()
	end
end

function virtual_actions:getStatus(time)

	if self.GeneratedActions and #self.GeneratedActions > 0 then

	    local firstAction, lastAction, nbActions = self:getFirstLastAndNbActions()	
		return nbActions .. ' actions, ' .. lastAction.at - firstAction.at .. ' secs', nil
	else
		local script = ofs.Script(self.ScriptIdx)
		local closestAction = script:closestActionAfter(time)
		if closestAction then
			return 'empty, next action in ' .. closestAction.at - time .. ' secs', nil
		else
			return 'empty', nil
		end
	end

end

function virtual_actions:updateDebugScriptIfNeeded(userAction, actions)
    -- Loop through all scripts
    for i = 1, ofs.ScriptCount(), 1 do
        local scriptName = ofs.ScriptName(i)
        
        -- Check if script contains "debugFSTB"
        if string.find(scriptName, "debugFSTB") then
            local debugScript = ofs.Script(i)
            
            -- Remove all points from the debug script
            for idx in pairs(debugScript.actions) do
                debugScript:markForRemoval(idx)
            end
            debugScript:removeMarked()
            
            -- Add 3 points for each action from the received data
            for _, action in ipairs(actions) do
                -- Set position variables based on action.pos
                local limitPos, defaultPos
                
                if action.pos == 0 then
                    -- For bottom positions
                    limitPos = 20
                    defaultPos = 40
                else
                    -- For top positions
                    limitPos = 90
                    defaultPos = 70
                end
                
                -- Add points using the position variables
                -- Add point at AtMin with limitPos if at != AtMin
                if action.at ~= action.AtMin then
                    debugScript.actions:add(Action.new(action.AtMin / 1000.0, limitPos, false))
                end
                
                -- Always add point at actual position with defaultPos
                debugScript.actions:add(Action.new(action.at / 1000.0, defaultPos, false))
                
                -- Add point at AtMax with limitPos if at != AtMax
                if action.at ~= action.AtMax then
                    debugScript.actions:add(Action.new(action.AtMax / 1000.0, limitPos, false))
                end
            end
            
            -- Commit changes to the debug script
            debugScript:commit()
            
            if config.EnableLogs then 
                printWithTime(userAction, 'updateDebugScriptIfNeeded', 'Updated debug script: ' .. scriptName) 
            end
        end
    end
end

return virtual_actions
