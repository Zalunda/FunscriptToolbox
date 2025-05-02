-- FunscriptToolbox.MotionVectors LUA virtual_actions
local virtual_actions = {}

function virtual_actions:new(scriptIdx, config)
	local o = { 
		ScriptIdx = scriptIdx,		
		Config = config,
		FrameDurationInSec = nil,
		GeneratedActionsOriginal = {},
		ActionsInTimeline = nil
	}
	setmetatable(o, {__index = virtual_actions})
	return o
end

function virtual_actions:init(userAction, actions, frameDurationInSec)
	self.GeneratedActionsOriginal = {}
	self:removeAllVirtualActionsInTimelime(userAction .. ' init')
	if #actions > 0 then	
		self:updateDebugScriptIfNeeded(userAction, actions)

		for i, action in ipairs(actions) do	
			local new_action = { 
				originalAt = action.at / 1000.0,
				originalPos = action.pos,
				isTop = action.pos ~= 0,
				at = action.at / 1000.0,
				atMin = action.AtMin,
				atMax = action.AtMax,
				pos = action.pos,
				locked = false
			}
			printWithTime(userAction, 'new_action', new_action.at)

			table.insert(self.GeneratedActionsOriginal, new_action)
		end

		-- Try to 'attach' the first and last generated actions to existing actions point
		local script = ofs.Script(self.ScriptIdx)
		local firstGeneratedAction = self.GeneratedActionsOriginal[1]
		local firstActionMatched = script:closestAction(firstGeneratedAction.at)
		if firstActionMatched and math.abs(firstGeneratedAction.at - firstActionMatched.at) < frameDurationInSec then
			printWithTime(userAction, 'locking first point to existing point', getFormattedTime(firstActionMatched.at))
			firstGeneratedAction.at = firstActionMatched.at
			firstGeneratedAction.originalAt = firstActionMatched.at
			firstGeneratedAction.locked = false
		end

		local lastGeneratedAction = self.GeneratedActionsOriginal[#self.GeneratedActionsOriginal]
		local lastActionMatched = script:closestAction(lastGeneratedAction.at)
		if lastActionMatched and math.abs(lastGeneratedAction.at - lastActionMatched.at) < frameDurationInSec then
			printWithTime(userAction, 'locking last point to existing point', getFormattedTime(lastActionMatched.at))
			lastGeneratedAction.at = lastActionMatched.at
			lastGeneratedAction.originalAt = lastActionMatched.at
			lastGeneratedAction.locked = false
		end

		if config.EnableLogs then printWithTime(userAction, 'init.insert', #self.GeneratedActionsOriginal) end

		self.FrameDurationInSec = frameDurationInSec
		self:update(userAction .. ' init')
	end
end


-- Revised function to identify full strokes from a list of actions
local function identifyFullStrokes(actions)
    local fullStrokes = {}
    local i = 1
    
    while i <= #actions - 2 do
        local point1 = actions[i]
        local point2 = actions[i+1]
        local point3 = actions[i+2]
        
        -- Check if we have a valid bottom-top-bottom pattern
        -- Using isTop property if available, otherwise use position
        local isPattern = false
        
        if point1.isTop ~= nil and point2.isTop ~= nil and point3.isTop ~= nil then
            -- Use isTop property
            isPattern = (not point1.isTop and point2.isTop and not point3.isTop)
        else
            -- Fallback to position comparison
            isPattern = (point1.pos < point2.pos and point3.pos < point2.pos)
        end
        
        if isPattern then
            -- This is a full stroke (bottom-top-bottom)
            table.insert(fullStrokes, FullStroke:new(point1, point2, point3))
            i = i + 2  -- Move to the start of next potential stroke
        else
            i = i + 1  -- Move to next point
        end
    end
    
    return fullStrokes
end

-- Function to update actions with optimized stroke timings
local function updateActionsWithOptimizedStrokes(actions, fullStrokes)
    local strokeIndex = 1
    local i = 1
    
    while i <= #actions - 2 and strokeIndex <= #fullStrokes do
        local point1 = actions[i]
        local point2 = actions[i+1]
        local point3 = actions[i+2]
        
        -- Check if we have a valid bottom-top-bottom pattern
        local isPattern = false
        
        if point1.isTop ~= nil and point2.isTop ~= nil and point3.isTop ~= nil then
            -- Use isTop property
            isPattern = (not point1.isTop and point2.isTop and not point3.isTop)
        else
            -- Fallback to position comparison
            isPattern = (point1.pos < point2.pos and point3.pos < point2.pos)
        end
        
        if isPattern then
            -- Update with optimized stroke timings
            local optimizedStroke = fullStrokes[strokeIndex]
            
            -- Update original actions with optimized timings
            -- Make sure we maintain order and minimum spacing
            point2.at = optimizedStroke.top.at
            point3.at = optimizedStroke.bottom2.at
            
            -- Ensure minimum spacing
            if i+3 <= #actions and point3.at >= actions[i+3].at then
                point3.at = actions[i+3].at - 0.01  -- Maintain spacing
            end
            
            strokeIndex = strokeIndex + 1
            i = i + 2  -- Move to the next potential stroke
        else
            i = i + 1  -- Move to next point
        end
    end
    
    return actions
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

function virtual_actions:removeAllVirtualActionsInTimelime(userAction)

	local script = ofs.Script(self.ScriptIdx)
	if self.ActionsInTimeline and #self.ActionsInTimeline > 0 then		
		local firstAction = self.ActionsInTimeline[1]
		local lastAction = self.ActionsInTimeline[#self.ActionsInTimeline]
		
		local logsInfo = ''
		for idx, action in ipairs(script.actions) do
			if action.at >= firstAction.at and action.at <= lastAction.at then
				script:markForRemoval(idx)
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
			end
		end
		script:removeMarked()
		script:commit()
		
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'removeAllVirtualActionsInTimelime', #self.ActionsInTimeline .. '=>0', logsInfo) end
	end
end

function virtual_actions:unvirtualizeActionsBefore(userAction, time)
	
 	if self.GeneratedActionsOriginal then
		local logsInfo = ''
		for i, action in ipairs(self.GeneratedActionsOriginal) do
			if action.at <= time and action.enabled then
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
				action.enabled = false
			end
		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'unvirtualizeActionsBefore.GeneratedActionsOriginal', #self.GeneratedActionsOriginal, logsInfo) end
 	end

 	if self.ActionsInTimeline then
		local logsInfo = ''
 		while #self.ActionsInTimeline > 0 and self.ActionsInTimeline[1].at <= time do
			if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(self.ActionsInTimeline[1].at) .. ', ' end
 			table.remove(self.ActionsInTimeline, 1)
 		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'unvirtualizeActionsBefore.ActionsInTimeline', #self.ActionsInTimeline, logsInfo) end
 	end
end

function virtual_actions:deleteAllVirtualActions(userAction)

	if config.EnableLogs then printWithTime(userAction, 'deleteAllVirtualActions') end
	self:removeAllVirtualActionsInTimelime(userAction .. ' deleteAllVirtualActions')
	self.ActionsInTimeline = {}
	self.GeneratedActionsOriginal = {}
end

function virtual_actions:getStartTime()
	if #self.ActionsInTimeline > 0 then
		return self.ActionsInTimeline[1].at
	else
		return nil
	end
end

function virtual_actions:update(userAction)

	self:removeAllVirtualActionsInTimelime(userAction .. ' update')

	if self.GeneratedActionsOriginal and #self.GeneratedActionsOriginal > 0 then

		local script = ofs.Script(self.ScriptIdx)

		self.ActionsInTimeline = {}

		-- 1. Add the points, ajusting the at with top/bottom offset
		local zoneStartAction = nil
		local zoneEndAction = nil
		for i, action in ipairs(self.GeneratedActionsOriginal) do	
			if action.enabled then 
				if not zoneStartAction then
					zoneStartAction = script:closestAction(action.at)
					zoneEndAction = script:closestActionAfter(action.at + self.FrameDurationInSec / 2)
				end
			
				local newAt = action.at
				if action.isTop then
					newAt = action.at + self.FrameDurationInSec * self.Config.TopPointsOffset
				else
					newAt = action.at + self.FrameDurationInSec * self.Config.BottomPointsOffset
				end
				
				local new_action = Action.new(newAt, action.pos, false)
				table.insert(self.ActionsInTimeline, new_action)
			end
		end	
		local zoneStart = zoneStartAction and zoneStartAction.at or 0
		local zoneEnd = zoneEndAction and zoneEndAction.at or 10000000
		if config.EnableLogs then printWithTime(userAction, 'update', 'zoneStart', getFormattedTime(zoneStart)) end
		if config.EnableLogs then printWithTime(userAction, 'update', 'zoneEnd', getFormattedTime(zoneEnd)) end

		-- 2. Make sure that actions are still in order and don't overlap (because of the top/bottom offset).
		for i = 2, #self.ActionsInTimeline do
			local indexToFix = i
			while indexToFix > 1 and self.ActionsInTimeline[indexToFix - 1].at >= self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec / 2 do
				if config.EnableLogs then printWithTime(userAction, 'update', 'fixing', self.ActionsInTimeline[indexToFix - 1].at, 'to', self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec) end
				self.ActionsInTimeline[indexToFix - 1].at = self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec
				indexToFix = indexToFix - 1
			end
		end
			
		-- Adjust position, according to the user preference
		local totalAmplitude = self.Config.MaximumPosition - self.Config.MinimumPosition
		local ratio = totalAmplitude / 100
		local minAmplitude = self.Config.MinimumPercentageFilled * ratio		
		local amplitudeRange = (100 - self.Config.MinimumPercentageFilled) * ratio

		local previousPos = self.ActionsInTimeline[1].pos
		for i = 2, #self.ActionsInTimeline do
			local currentPos = self.ActionsInTimeline[i].pos;
			
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
				 	self.ActionsInTimeline[i - 1].pos = minPosition
				 	self.ActionsInTimeline[i].pos = maxPosition
				else
				 	self.ActionsInTimeline[i - 1].pos = maxPosition
				 	self.ActionsInTimeline[i].pos = minPosition
				end
				previousPos = currentPos
			end
		end
		
		-- Remove the item that would be outside our 'zone' (i.e. they might overlap existing actions)
		local logsInfo = ''
		for i = #self.ActionsInTimeline, 1, -1 do
			local action = self.ActionsInTimeline[i]
			local closestAction = script:closestAction(action.at)
			if action.at <= zoneStart or action.at >= zoneEnd then
				if config.EnableLogs then logsInfo = logsInfo .. '[SKIP-zone] ' .. getFormattedTime(action.at) .. ', ' end
				table.remove(self.ActionsInTimeline, i)
			elseif closestAction and math.abs(closestAction.at - action.at) < self.FrameDurationInSec / 2 then
				if config.EnableLogs then logsInfo = logsInfo .. '[SKIP-too-close] ' .. getFormattedTime(action.at) .. ', ' end
			    table.remove(self.ActionsInTimeline, i)
			else
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
			end
		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'update', #self.ActionsInTimeline, logsInfo) end

		-- self:debugSameTimestamp(zoneStart, zoneEnd)

		-- Finally, add the points to OFS timeline
		for i, action in ipairs(self.ActionsInTimeline) do
			script.actions:add(action)
		end

		script:commit()
	end
end

function virtual_actions:debugSameTimestamp(zoneStart, zoneEnd)
	
	printWithTime('---debugSameTimestamp---')
	printWithTime('zoneStart ' .. zoneStart)
	printWithTime('zoneEnd ' .. zoneEnd)
	local script = ofs.Script(self.ScriptIdx)
	for i, action in ipairs(self.ActionsInTimeline) do		
		local closest = script:closestAction(action.at)
		if closest and action.at == closest.at then
			printWithTime('bug at ' .. action.at .. ' (' .. i .. ')')
		end
	end
	printWithTime('------------------------')
end

function virtual_actions:getStatus(time)

	if self.ActionsInTimeline and #self.ActionsInTimeline > 0 then
		local firstAction = self.ActionsInTimeline[1]
		local lastAction = self.ActionsInTimeline[#self.ActionsInTimeline]
		return #self.ActionsInTimeline .. ' actions, ' .. lastAction.at - firstAction.at .. ' secs', nil
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

return virtual_actions
