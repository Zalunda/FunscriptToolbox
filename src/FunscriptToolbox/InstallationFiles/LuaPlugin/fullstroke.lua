-- FullStroke class to represent a complete stroke (low-high-low)
local FullStroke = {}

function FullStroke:new(bottom1, top, bottom2)
    local o = {
        bottom1 = bottom1, -- First bottom point
        top = top,         -- Top/peak point
        bottom2 = bottom2, -- Second bottom point
        
        -- Calculate properties
        duration = bottom2.at - bottom1.at,       -- Total stroke duration
        upDuration = top.at - bottom1.at,         -- Duration of upstroke
        downDuration = bottom2.at - top.at,       -- Duration of downstroke
        ratio = (top.at - bottom1.at) / (bottom2.at - bottom1.at) -- Position ratio of peak
    }
    setmetatable(o, {__index = FullStroke})
    return o
end

-- Calculate similarity percentage between this stroke and another
function FullStroke:similarityPercent(otherStroke)
    -- Calculate duration difference as percentage
    local durationDiff = math.abs(self.duration - otherStroke.duration) / math.max(self.duration, otherStroke.duration) * 100
    
    -- Calculate ratio difference as percentage (peak position difference)
    local ratioDiff = math.abs(self.ratio - otherStroke.ratio) * 100
    
    -- Return the maximum difference (worst similarity)
    return math.max(durationDiff, ratioDiff)
end

-- Adjust timing of this stroke to better match a target pattern
function FullStroke:adjustTimingToMatch(targetStroke, weight)
    weight = weight or 0.5 -- Default to 50% adjustment
    
    -- Calculate ideal timestamps based on target pattern
    local idealTop = self.bottom1.at + (targetStroke.duration * targetStroke.ratio)
    local idealBottom2 = self.bottom1.at + targetStroke.duration
    
    -- Apply weighted adjustment
    local newTopAt = self.top.at + (idealTop - self.top.at) * weight
    local newBottom2At = self.bottom2.at + (idealBottom2 - self.bottom2.at) * weight
    
    -- Update points
    self.top.at = newTopAt
    self.bottom2.at = newBottom2At
    
    -- Recalculate properties
    self.duration = self.bottom2.at - self.bottom1.at
    self.upDuration = self.top.at - self.bottom1.at
    self.downDuration = self.bottom2.at - self.top.at
    self.ratio = self.upDuration / self.duration
    
    return self
end

-- Calculate error value for this stroke compared to target pattern
function FullStroke:calculateErrorValue(targetStroke)
    local durationError = math.abs(self.duration - targetStroke.duration)
    local ratioError = math.abs(self.ratio - targetStroke.ratio) * self.duration
    
    return durationError + ratioError * 2  -- Weight ratio errors more heavily
end