-- as clearly visible, this script is courtesy of Rhox and the OVN team, thanks a bunch. Only the obvious changes and the faction list. 


local function rhox_teb_create_emp_army_buttons()
	local parent_ui = find_uicomponent(core:get_ui_root(), "hud_campaign", "hud_center_docker", "hud_center", "small_bar",  "button_subpanel_parent", "button_subpanel", "button_group_army");

    local result = core:get_or_create_component("rhox_grudge_camp_button", "ui/campaign ui/rhox_teb_deploy_camp_button.twui.xml", parent_ui)
    
    
    local result2 = core:get_or_create_component("rhox_grudge_camp_panel", "ui/campaign ui/rhox_teb_camp_panel.twui.xml", core:get_ui_root())
    
    
end


local teb_faction_keys = {
    "mixer_teb_bilbali",
    "mixer_teb_catrazza",
    "mixer_teb_colombo",
    "mixer_teb_gashnag",
    "mixer_teb_border_princes",
    "mixer_teb_estalia",
    "mixer_teb_tilea",
    "mixer_teb_new_world_colonies",
    "cr_teb_central_confederacy",
    "cr_teb_luccini",
    "cr_teb_magritta",
    "cr_teb_miragliano",
    "cr_teb_order_of_the_blazing_sun",
    "cr_teb_pavona"
}

-- improvised function to use the list above instead of a single faction key like in the original Grudgebringer script.
local function is_teb_faction(faction_key)
    for _, teb_key in ipairs(teb_faction_keys) do
        if faction_key == teb_key then
            return true
        end
    end
    return false
end

cm:add_first_tick_callback(
    function(context) 
        local local_faction = cm:get_local_faction_name(true)
        if is_teb_faction(local_faction) then
            core:add_listener(
                "rhox_teb_army_panel_opened",
                "PanelOpenedCampaign",
                function(context) return context.string == "units_panel" end,
                function(context)
                    cm:real_callback(rhox_teb_create_emp_army_buttons, 2);
                end,
                true
            );
            
            core:add_listener(
                "rhox_teb_CharacterSelected_horde_growth_shower",
                "CharacterSelected",
                function(context)
                    return context:character():faction():name() == local_faction 
                        and context:character():character_subtype_key() == "teb_merc_general_camp";
                end,
                function(context)
                    cm:callback(
                        function()
                            local horde_growth = find_uicomponent(core:get_ui_root(), "hud_campaign", "info_panel_holder", "horde_growth");
                            if horde_growth then 
                                horde_growth:SetVisible(true)
                            end
                        end,
                        0.1
                    )
                end,
                true
            )
        end
    end
)
