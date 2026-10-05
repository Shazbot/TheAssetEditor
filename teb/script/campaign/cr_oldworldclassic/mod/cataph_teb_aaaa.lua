--account for the fact that most of these won't have actual barracks until they grow up and build them
-- thanks to ChaosRobie for the scropt!

local teb_factions = {
--    cr_teb_magritta       = "estalia",
    cr_teb_almagora       = "estalia",
    cr_teb_bilbali        = "estalia",
    cr_teb_gualcazar      = "estalia",
    cr_teb_nuja           = "estalia",
    cr_teb_reinos_pobres  = "irrana",
    cr_teb_vizeaya        = "estalia",
    --cr_teb_estalia_rebels = "estalia",
--    cr_teb_luccini                  = "tilea",
--    cr_teb_miragliano               = "tilea",
    cr_teb_monte_castello           = "tilea",
 --   cr_teb_order_of_the_blazing_sun = "tilea",
    cr_teb_pavona                   = "tilea",
    cr_teb_remas                    = "tilea",
    cr_teb_tobaro                   = "tilea",
    cr_teb_trantio                  = "tilea",
    cr_teb_verezzo                  = "tilea",
    --cr_teb_tilea_rebels             = "tilea",
    cr_teb_central_confederacy   = "border",
    cr_teb_south_confederacy     = "border",
--    cr_teb_north_confederacy     = "border",
    --cr_teb_border_princes_rebels = "border",
--    cr_teb_pigbarter    = "border",
--    cr_teb_estebans_caballeros     = "estalia",
}

local army_templates = {
    estalia = {
        "teb_xbowmen",
        "teb_xbowmen",
        "teb_xbowmen",
        "teb_conqui_adventurers",
        "teb_billmen",
        "teb_billmen",
        "teb_conqui_riders",
        "teb_pikemen",
    },
    irrana = {
        "teb_xbowmen",
        "teb_xbowmen",
        "teb_irrana",
        "teb_irrana",
        "teb_irrana",
        "teb_irrana",
        "teb_militia_spearmen",
        "teb_militia_spearmen",
        "teb_conqui_lancers", --I know those guys will have the life expectancy of a beached jellyfish but eh.
    },
    tilea = {
        "teb_xbowmen",
        "teb_xbowmen",
        "teb_light_scouts",
        "teb_pavisiers",
        "teb_half_pikes",
        "teb_half_pikes",
        "teb_pikemen",
        "teb_pikemen",
    },
    border = {
        "teb_xbowmen",
        "teb_xbowmen",
        "teb_militia_spearmen",
        "teb_militia_spearmen",
        "teb_billmen",
        "teb_billmen",
        "teb_light_scouts",
        "teb_light_scouts",
    }
}

local remove_these_buildings = {
    "wh_main_emp_barracks_1",
    "wh_main_emp_barracks_2",
    "wh_main_emp_barracks_3",
}

cm:add_first_tick_callback(
    function()
        if cm:is_new_game() then
            for faction_name, subculture in pairs(teb_factions) do
                local faction = cm:get_faction(faction_name)
                if faction then
                    --Fix building
                    for _, slot in model_pairs(faction:home_region():slot_list()) do
                        if slot:has_building() then
                            for i = 1, #remove_these_buildings do
                                if slot:building():name() == remove_these_buildings[i] then
                                    cm:region_slot_instantly_dismantle_building(slot)
-- this no worky but ideally I need to replace the thing                                        cm:instantly_upgrade_building_in_region(slot,"teb_mil_city_1")
                                    break
                                end
                            end
                        end
                    end
                
                    --Adjust faction leader starting army
                    local faction_leader = faction:faction_leader()
                    
                    if faction_leader then
                        local faction_leader_str = cm:char_lookup_str(faction_leader:command_queue_index());
                        
                        -- Remove all units from the faction leader
                        cm:remove_all_units_from_general(faction_leader)
                        
                        -- Then add in the replacements, depending on the factions 'subculture'
                        local unit_list = army_templates[subculture]
                        for _, unit in ipairs(unit_list) do
                            cm:grant_unit_to_character(faction_leader_str, unit)
                        end
                    end
                end
            end
        end
    end
)



