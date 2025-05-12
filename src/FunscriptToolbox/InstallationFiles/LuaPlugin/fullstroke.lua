-- fullstroke class to represent a complete stroke (low-high-low)
local fullstroke = {}

-- Static method to get actions for the last N half-strokes
function fullstroke.getActionsForLastNHalfStrokes(actions, maxNumberHalfStrokes)
    -- Need at least 2 actions to detect direction
    if #actions < 2 then
        return actions
    end
    
    local actionsToInclude = {}
    local directionChanges = 0
    local previousDirection = nil
    
    -- Start from the most recent action (end of array) and work backwards
    -- Always include the newest action
    table.insert(actionsToInclude, actions[#actions])
    
    for i = #actions - 1, 1, -1 do
        local current = actions[i]
        local next = actions[i+1]  -- The action after (more recent in time)
        
        -- Determine direction (still from older to newer perspective)
        local direction = nil
        if current.pos < next.pos then
            direction = "up"
        elseif current.pos > next.pos then
            direction = "down"
        else
            direction = "neutral"
        end
        
        -- Check for direction change (skip neutral)
        if direction ~= "neutral" then
            if previousDirection ~= nil and direction ~= previousDirection then
                directionChanges = directionChanges + 1
                
                -- If we found enough direction changes, we're done
                if directionChanges >= maxNumberHalfStrokes then
                    break
                end
            end
            
            -- Update previous direction
            previousDirection = direction
        end

        -- Include this action
        table.insert(actionsToInclude, 1, current)  -- Insert at beginning to maintain chronological order        
    end
    
    return actionsToInclude
end

-- Static method to identify full strokes from a list of actions
function fullstroke.identifyFullStrokes(actions)
    local fullStrokes = {}
    local i = 1
    local pointsNotInStrokes = 0
    
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
            table.insert(fullStrokes, fullstroke:new(point1, point2, point3))
            i = i + 2  -- Move to the start of next potential stroke
        else
            i = i + 1  -- Move to next point
            pointsNotInStrokes = pointsNotInStrokes + 1
        end
    end
    
    return fullStrokes, pointsNotInStrokes
end

function fullstroke:new(bottom1, top, bottom2)
    local o = {
        bottom1 = bottom1, -- First bottom point
        top = top,         -- Top/peak point
        bottom2 = bottom2, -- Second bottom point
    }
    setmetatable(o, {__index = fullstroke})
    return o
end

-- Optimize stroke timings based on surrounding strokes
function fullstroke.optimizeActionPointTimings(fullstrokes, similarityThreshold, minRatioUpDown, maxRatioUpDown)

    -- TimeRange helper functions
    local function isInRange(time, point)
        return time >= point.atMin and time <= point.atMax
    end
    
    local function applyLimit(time, point)
        if time < point.atMin then
            return point.atMin
        elseif time > point.atMax then
            return point.atMax
        else
            return time
        end
    end
    
    local function getPointErrorValue(point)
        if not point.atMin or not point.atMax then
            return 0
        end
        
        local time = point.at
        if time < point.atMin then
            return (point.atMin - time) * 10
        elseif time > point.atMax then
            return (time - point.atMax) * 10
        else
            return 0
        end
    end
    
    -- Stroke-related helper functions
    local function getDuration(stroke)
        return stroke.bottom2.at - stroke.bottom1.at
    end
    
    local function getUpDuration(stroke)
        return stroke.top.at - stroke.bottom1.at
    end
    
    local function getDownDuration(stroke)
        return stroke.bottom2.at - stroke.top.at
    end
    
    local function getUpPercentage(stroke)
        return getUpDuration(stroke) / getDuration(stroke) * 100
    end
    
    local function getStrokesErrorValue(stroke, previousStroke)
        local error = 0
    
        -- Add error from current stroke's points
        error = error + getPointErrorValue(stroke.bottom1)
        error = error + getPointErrorValue(stroke.top)
        error = error + getPointErrorValue(stroke.bottom2)
    
        -- Add error from previous stroke's points, only if its consecutive (i.e. share a point)
        if previousStroke and previousStroke.bottom2 == stroke.bottom1 then
            -- TODO Should I check previous Strokes???  I think not.
            error = error + getPointErrorValue(previousStroke.bottom1)
            error = error + getPointErrorValue(previousStroke.top)
            -- Don't count previousStroke.bottom2 because it's the same point as stroke.bottom1

            local prevUpPerc = getUpPercentage(previousStroke)
            if prevUpPerc < minRatioUpDown then
                error = error + (minRatioUpDown - prevUpPerc) * 50
            elseif prevUpPerc > maxRatioUpDown then
                error = error + (prevUpPerc - maxRatioUpDown) * 50
            end
        end
    
        local upPerc = getUpPercentage(stroke)
        if upPerc < minRatioUpDown then
            error = error + (minRatioUpDown - upPerc) * 50
        elseif upPerc > maxRatioUpDown then
            error = error + (upPerc - maxRatioUpDown) * 50
        end
    
        return error
    end

    local function isWithinThreshold(value1, value2, threshold)
        if value1 == 0 or value2 == 0 then
            return false
        end
        
        local diff = math.abs(value1 - value2)
        local avg = (value1 + value2) / 2.0
        
        return diff <= (avg * threshold)
    end
    
    -- Need enough strokes to do the comparison
    if #fullstrokes < 3 then
        return
    end
    
    -- First pass: Check for inconsistent durations between consecutive strokes
    for i = 2, #fullstrokes - 1 do
        local previousStroke = fullstrokes[i - 1]
        local currentStroke = fullstrokes[i]
        local nextStroke = fullstrokes[i + 1]

        if previousStroke.bottom2 ~= currentStroke.bottom1 or currentStroke.bottom2 ~= nextStroke.bottom1 then
            printWithTime('SKIPPING-MissingNeighbor', i);
            goto continue
        end

        -- Calculate the average duration of the current pair
        local averageDuration3 = (getDuration(previousStroke) + getDuration(currentStroke) + getDuration(nextStroke)) / 3.0

        if not isWithinThreshold(getDuration(previousStroke), averageDuration3, similarityThreshold) or
           not isWithinThreshold(getDuration(currentStroke), averageDuration3, similarityThreshold) or
           not isWithinThreshold(getDuration(nextStroke), averageDuration3, similarityThreshold) then

            printWithTime('SKIPPING-NotSimilar', i);
            goto continue
        end
        
        printWithTime('Optimizing', i);
        local originalPreviousAt = previousStroke.at
        local originalCurrentAt = currentStroke.at
        local originalNextAt = nextStroke.at

        currentStroke.bottom1.at = clamp(previousStroke.bottom1.at + averageDuration3, currentStroke.bottom1.atMin, currentStroke.bottom1.atMax)
        printWithTime('    bottom1.at', currentStroke.bottom1.originalAt, currentStroke.bottom1.at);

        local averageDuration2 = (getDuration(currentStroke) + getDuration(nextStroke)) / 2.0
        currentStroke.bottom2.at = clamp(currentStroke.bottom1.at + averageDuration2, currentStroke.bottom2.atMin, currentStroke.bottom2.atMax)
        printWithTime('    bottom2.at', currentStroke.bottom2.originalAt, currentStroke.bottom2.at);

        goto continue
        
        -- Check if surrounding strokes are similar to the average
        local allSurroundingStrokesSimilar = true
        for j = 1, surroundingStrokesToCheck do
            local prevStroke = fullstrokes[i - j]
            local nextPlusStroke = fullstrokes[i + 1 + j]
            
            if not isWithinThreshold(getDuration(prevStroke), averageDuration, similarityThreshold) or
               not isWithinThreshold(getDuration(nextPlusStroke), averageDuration, similarityThreshold) then
                allSurroundingStrokesSimilar = false
                break
            end
        end
        
        -- Find the shared point between current and next strokes
        if currentStroke.bottom2 == nextStroke.bottom1 and allSurroundingStrokesSimilar then
            -- Calculate how much we need to adjust
            local currentDuration = getDuration(currentStroke)
            local nextDuration = getDuration(nextStroke)
            local adjustmentAmount = (nextDuration - currentDuration) / 2.0
                
            -- Apply the adjustment (respecting point time range limits)
            local sharedPoint = currentStroke.bottom2
            local newTime = applyLimit(sharedPoint.at + adjustmentAmount, sharedPoint)
            sharedPoint.at = newTime
            -- Shared point is automatically updated in both strokes since they reference the same object
        end
        ::continue::
    end
    
    -- Second pass: Fine-tune with small increments
    local increment = 0.005  -- 5ms increment
    local nbUpdates
    
    repeat
        nbUpdates = 0
        
        for i = 1, #fullstrokes do
            local previousStroke = (i > 1) and fullstrokes[i - 1] or nil
            local stroke = fullstrokes[i]
            
            -- Try different adjustment strategies
            local function tryUpdateTiming(increment, updateTimingAction)
                local currentError = getStrokesErrorValue(stroke, previousStroke)
                updateTimingAction(increment)
                local newError = getStrokesErrorValue(stroke, previousStroke)
                
                if newError < currentError then
                    return 1  -- Improvement found
                else
                    updateTimingAction(-increment)  -- Revert change
                    return 0  -- No improvement
                end
            end
            
            -- Try adjusting the top point time
            nbUpdates = nbUpdates + tryUpdateTiming(increment, function(inc)
                stroke.top.at = stroke.top.at + inc
            end)
            
            -- Try adjusting the top point time in opposite direction
            nbUpdates = nbUpdates + tryUpdateTiming(increment, function(inc)
                stroke.top.at = stroke.top.at - inc
            end)
            
            -- Try moving both bottom1 and top together
            nbUpdates = nbUpdates + tryUpdateTiming(increment, function(inc)
                stroke.bottom1.at = stroke.bottom1.at + inc
                stroke.top.at = stroke.top.at + inc
            end)
            
            -- Try moving both bottom1 and top together in opposite direction
            nbUpdates = nbUpdates + tryUpdateTiming(increment, function(inc)
                stroke.bottom1.at = stroke.bottom1.at - inc
                stroke.top.at = stroke.top.at - inc
            end)
            
            -- Try adjusting only bottom1
            nbUpdates = nbUpdates + tryUpdateTiming(increment, function(inc)
                stroke.bottom1.at = stroke.bottom1.at + inc
            end)
            
            -- Try adjusting only bottom1 in opposite direction
            nbUpdates = nbUpdates + tryUpdateTiming(increment, function(inc)
                stroke.bottom1.at = stroke.bottom1.at - inc
            end)
        end
    until nbUpdates == 0
end


function fullstroke.writeDebugTimingFile(fullstrokes, filename)
    -- Check if there are strokes to write
    if not fullstrokes or #fullstrokes == 0 then
        return
    end
    
    -- Open file for writing
    local file = io.open(filename, "w")
    if not file then
        printWithTime("Error: Could not open file for writing:", filename)
        return
    end
      
    -- Calculate the percentage position of a time value between min and max constraints
    local function calculateTimePercentage(time, timeMin, timeMax)
	    local range = timeMax - timeMin
	    if range == 0 then
	       return 0
	    end
        return clamp(
		    math.floor(((time - timeMin) / range) * 100 + 0.5), -- Round to nearest integer
		    -999,
		    999)
    end

    -- For each stroke
    for i, stroke in ipairs(fullstrokes) do
        local bottom1 = stroke.bottom1
        local top = stroke.top
        local bottom2 = stroke.bottom2
        
        if bottom1 and top and bottom2 then
            -- Calculate original values (using milliseconds)
            local origB1Time = bottom1.originalAt
            local origTopTime = top.originalAt
            local origB2Time = bottom2.originalAt
            local origDuration = origB2Time - origB1Time
            local origUpTime = origTopTime - origB1Time
            local origUpPercent = origDuration > 0 and math.floor((origUpTime / origDuration) * 100 + 0.5) or 0

            -- Calculate percentages for points vs AtMin/AtMax
            local origB1Percent = calculateTimePercentage(bottom1.originalAt, bottom1.atMin, bottom1.atMax)
            local origTopPercent = calculateTimePercentage(top.originalAt, top.atMin, top.atMax)
            local origB2Percent = calculateTimePercentage(bottom2.originalAt, bottom2.atMin, bottom2.atMax)
            
            -- Calculate current values
            local currB1Time = bottom1.at
            local currTopTime = top.at
            local currB2Time = bottom2.at
            local currDuration = currB2Time - currB1Time
            local currUpTime = currTopTime - currB1Time
            local currUpPercent = currDuration > 0 and math.floor((currUpTime / currDuration) * 100 + 0.5) or 0
            
            -- Calculate percentages for points vs AtMin/AtMax
            local currB1Percent = calculateTimePercentage(bottom1.at, bottom1.atMin, bottom1.atMax)
            local currTopPercent = calculateTimePercentage(top.at, top.atMin, top.atMax)
            local currB2Percent = calculateTimePercentage(bottom2.at, bottom2.atMin, bottom2.atMax)
            
            -- Calculate differences
            local diffB1Time = currB1Time - origB1Time
            local diffTopTime = currTopTime - origTopTime
            local diffB2Time = currB2Time - origB2Time
            local diffDuration = currDuration - origDuration
            local diffUpPercent = currUpPercent - origUpPercent
            local diffB1Percent = currB1Percent - origB1Percent
            local diffTopPercent = currTopPercent - origTopPercent
            local diffB2Percent = currB2Percent - origB2Percent
            
            -- Format each line
            file:write(string.format("%8.3f %8.3f %8.3f %6.3f %3d %3d %3d %3d\n", 
                origB1Time, origTopTime, origB2Time, origDuration, origUpPercent, origB1Percent, origTopPercent, origB2Percent))            
            file:write(string.format("%8.3f %8.3f %8.3f %6.3f %3d %3d %3d %3d\n", 
                currB1Time, currTopTime, currB2Time, currDuration, currUpPercent, currB1Percent, currTopPercent, currB2Percent))           
            file:write(string.format("%8.3f %8.3f %8.3f %6.3f %3d %3d %3d %3d\n", 
                diffB1Time, diffTopTime, diffB2Time, diffDuration, diffUpPercent, diffB1Percent, diffTopPercent, diffB2Percent))
            
            file:write(string.rep("=", 80) .. "\n")
        end
    end
    
    -- Close the file
    file:close()
end

return fullstroke
