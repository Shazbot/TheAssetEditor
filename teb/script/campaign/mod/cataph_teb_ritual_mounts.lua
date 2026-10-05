

----------------------------------------------------------------------------------------------
----------------------------------------------------------------------------------------------
--
--THING TO MAKE MOUNTS FROM TEBUARY NOT SPAWN UGLY NOTIFICATION
--written by Groove

local function check_for_event()
    -- ModLog("Looking for the event")

    -- check if an event is open
    local panel = find_uicomponent(core:get_ui_root(), "events")
    if is_uicomponent(panel) and panel:VisibleFromRoot() then
        -- ModLog("Got the events component")

        ModLog("Fixing the event UIComponent.")
        -- get the event uicomponent
        local uic = find_uicomponent(panel, "event_layouts", "mount_aquired") -- <- sic

        if uic and uic:VisibleFromRoot() then
            local list = find_uicomponent(uic, "list")
            local content_holder = find_uicomponent(list, "content_holder")
        
            -- grab the arrow and character portrait and hide em
            local arrow = find_uicomponent(content_holder, "holder_arrow")
            local agent = find_uicomponent(content_holder, "holder_agent")

            local name = find_uicomponent(agent, "dy_agent_gained")
            local vis = true
            if name:GetStateText() == "dy_agent_name" then
                vis = false
            end

            -- ModLog("Got the UIComponent - setting the arrow and agent portrait to visible: " .. tostring(vis))
    
            arrow:SetVisible(vis)
            agent:SetVisible(vis)
        end
    end
end

local function kill_callback()
    core:get_tm():remove_repeat_callback("tebuary_fix_event")
end


local function start_callback()
    -- ModLog("Starting repeat callback!")
    core:get_tm():repeat_real_callback(function()
        check_for_event()
    end, 20, "tebuary_fix_event")
end

-- add more if ever adding more to add more mort mounts
local mount_rituals = {
    ["tebuary_4_35"] = true,
    ["tebuary_4_36"] = true,
}

local function init()
    -- ModLog("initializing the fix_mount_event file")

    -- only trigger any of the code if the local PC is a TEBian.
    -- it's MP safe since we're only listening to game events and tweaking UI on one PC
    local f = cm:get_local_faction(true)
    local f_name = f:name()
    if f:subculture() == "mixer_teb_southern_realms" then
        ModLog("starting listeners for " .. f_name)
        core:add_listener(
            "PlutarchEventFix",
            "RitualCompletedEvent",
            function(context)
                return 
                mount_rituals[context:ritual():ritual_key()]  -- Cataph: do `string.find(context:ritual():ritual_key(), "tebuary_mounts_")` instead, or whatever you want the prefix to be
                and context:performing_faction():name() == f_name
            end,
            function(context)
                -- ModLog("Mount ritual performed!")
                start_callback()

                -- stop the callback if this turn ends.
                core:add_listener(
                    "PlutarchEventGone",
                    "FactionTurnEnd",
                    true,
                    function(context)
                        kill_callback()
                    end,
                    false
                )
            end,
            true
        )
    end
end

cm:add_first_tick_callback(init)