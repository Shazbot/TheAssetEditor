local faction_leader_details = {
    ["cataph_teb_borgio_ime"] = {startpos_id = "1723362613", faction_key = "mixer_teb_tilea", land_unit_key = "teb_borgio_the_besieger", starting_units = {"teb_vespero","teb_besiegers","teb_pavisiers","teb_pikemen","teb_militia_spearmen","wh_main_emp_art_mortar"}}, 
    ["cataph_teb_catrazza_ime"] = {startpos_id = "102671841", faction_key = "mixer_teb_catrazza", land_unit_key = "teb_catrazza", starting_units = {"teb_montante_greatswords","teb_carabiniers","teb_xbow_cav","teb_pikemen","teb_xbowmen","teb_paymaster","teb_galloper"}}, 
    ["cataph_teb_lupio_ime"] = {startpos_id = "710497246", faction_key = "mixer_teb_estalia", land_unit_key = "teb_lupio", starting_units = {"teb_conqui_royal_guard","teb_handgunners","teb_conqui_adventurers","teb_billmen","teb_conqui_riders","wh_main_emp_art_mortar"}}, 
    ["cataph_teb_lucrezzia_ime"] = {startpos_id = "2054831964", faction_key = "mixer_teb_bilbali", land_unit_key = "teb_lucrezzia_belladonna", starting_units = {"teb_encarmine","teb_pavisiers","teb_paymaster","teb_half_pikes","teb_republican_guard"}}, 
    ["cataph_teb_gausser_ime"] = {startpos_id = "674409277", faction_key = "mixer_teb_border_princes", land_unit_key = "teb_gausser", starting_units = {"wh_main_emp_art_great_cannon","teb_freelance_knights","teb_pikemen","wh_dlc06_dwf_inf_bugmans_rangers_0","wh_main_dwf_inf_dwarf_warrior_1","teb_light_scouts"}}, 
    ["cataph_teb_gashnag_ime"] = {startpos_id = "1609508987", faction_key = "mixer_teb_gashnag", land_unit_key = "teb_gashnag", starting_units = {"teb_shieldbearers","teb_border_rangers","teb_guard_kossars","teb_billmen","teb_light_scouts"}}, 
    ["cataph_teb_cadavo_ime"] = {startpos_id = "2041809480", faction_key = "mixer_teb_new_world_colonies", land_unit_key = "teb_cadavo", starting_units = {"teb_light_cannon","teb_swash","teb_xbowmen","teb_pikemen","wh3_main_ogr_inf_maneaters_3","teb_conqui_riders"}}, 
    ["cataph_teb_colombo_ime"] = {startpos_id = "1325379312", faction_key = "mixer_teb_colombo", land_unit_key = "teb_colombo", starting_units = {"teb_noble_retinue","teb_xbowmen","teb_pikemen","teb_republican_guard","teb_militia_spearmen"}}, 
}

if vfs.exists("script/frontend/mod/cr_oldworldclassic_campaign_frontend.lua") then
    --TOW classic
    faction_leader_details={
        ["cataph_teb_borgio_tow"] = {startpos_id = "1905130419", faction_key = "cr_teb_miragliano", land_unit_key = "teb_borgio_the_besieger", starting_units = {"teb_vespero","teb_besiegers","teb_pavisiers","teb_pikemen","teb_militia_spearmen","wh_main_emp_art_mortar"}},
        ["cataph_teb_lupio_tow"] = {startpos_id = "1386731579", faction_key = "cr_teb_magritta", land_unit_key = "teb_lupio", starting_units = {"teb_conqui_royal_guard","teb_irrana","teb_conqui_lancers","teb_conqui_adventurers","teb_billmen","teb_conqui_riders","wh_main_emp_art_mortar"}},
        ["cataph_teb_gausser_tow"] = {startpos_id = "785415791", faction_key = "cr_teb_central_confederacy", land_unit_key = "teb_gausser", starting_units = {"wh_main_emp_art_great_cannon","teb_freelance_knights","teb_pikemen","wh_dlc06_dwf_inf_bugmans_rangers_0","wh_main_dwf_inf_dwarf_warrior_1","teb_light_scouts"}}, 
        ["cataph_teb_gashnag_tow"] = {startpos_id = "235439226", faction_key = "mixer_teb_gashnag", land_unit_key = "teb_gashnag", starting_units = {"teb_shieldbearers","teb_border_rangers","teb_guard_kossars","teb_billmen","teb_light_scouts"}},
        ["cataph_teb_lorenzo_lupo_tow"] = {startpos_id = "1108245816", faction_key = "cr_teb_luccini", land_unit_key = "teb_lorenzo_lupo", starting_units = {"wh2_dlc13_emp_cav_empire_knights_ror_1","teb_pavisiers","teb_republican_guard","teb_pikemen","teb_militia_spearmen"}},
        ["cataph_teb_colombo_tow"] = {startpos_id = "2134442793", faction_key = "cr_teb_order_of_the_blazing_sun", land_unit_key = "teb_colombo", starting_units = {"wh_dlc04_emp_cav_knights_blazing_sun_0","teb_republican_guard","teb_xbowmen","teb_pikemen"}}, 
--        ["cataph_teb_catrazza_tow"] = {startpos_id = "681801921", faction_key = "mixer_teb_catrazza", land_unit_key = "teb_catrazza", starting_units = {"teb_montante_greatswords","teb_carabiniers","teb_xbow_cav","teb_pikemen","teb_xbowmen","teb_paymaster","teb_galloper","wh3_main_ogr_inf_ironguts_0"}}, 
        ["cataph_teb_lucrezzia_tow"] = {startpos_id = "1146073257", faction_key = "cr_teb_pavona", land_unit_key = "teb_lucrezzia_belladonna", starting_units = {"teb_encarmine","teb_pavisiers","teb_paymaster","teb_half_pikes","teb_republican_guard"}}, 
        ["cataph_teb_cadavo_tow"] = {startpos_id = "1695644591", faction_key = "mixer_teb_new_world_colonies", land_unit_key = "teb_cadavo", starting_units = {"teb_light_cannon","teb_swash","teb_xbowmen","teb_pikemen","wh3_main_ogr_inf_maneaters_3","teb_conqui_riders"}}, 
    }
elseif vfs.exists("script/frontend/mod/cr_oldworld_campaign_frontend.lua") then
    --TOW
    faction_leader_details={
        ["cataph_teb_borgio_tow"] = {startpos_id = "1905130419", faction_key = "cr_teb_miragliano", land_unit_key = "teb_borgio_the_besieger", starting_units = {"teb_vespero","teb_besiegers","teb_pavisiers","teb_pikemen","teb_militia_spearmen","wh_main_emp_art_mortar"}},
        ["cataph_teb_lupio_tow"] = {startpos_id = "1386731579", faction_key = "cr_teb_magritta", land_unit_key = "teb_lupio", starting_units = {"teb_conqui_royal_guard","teb_irrana","teb_conqui_lancers","teb_conqui_adventurers","teb_billmen","teb_conqui_riders","wh_main_emp_art_mortar"}},
        ["cataph_teb_gausser_tow"] = {startpos_id = "785415791", faction_key = "cr_teb_central_confederacy", land_unit_key = "teb_gausser", starting_units = {"wh_main_emp_art_great_cannon","teb_freelance_knights","teb_pikemen","wh_dlc06_dwf_inf_bugmans_rangers_0","wh_main_dwf_inf_dwarf_warrior_1","teb_light_scouts"}}, 
        ["cataph_teb_gashnag_tow"] = {startpos_id = "1396713314", faction_key = "mixer_teb_gashnag", land_unit_key = "teb_gashnag", starting_units = {"teb_shieldbearers","teb_border_rangers","teb_guard_kossars","teb_billmen","teb_light_scouts"}},
        ["cataph_teb_lorenzo_lupo_tow"] = {startpos_id = "1108245816", faction_key = "cr_teb_luccini", land_unit_key = "teb_lorenzo_lupo", starting_units = {"wh2_dlc13_emp_cav_empire_knights_ror_1","teb_pavisiers","teb_republican_guard","teb_pikemen","teb_militia_spearmen"}},
        ["cataph_teb_colombo_tow"] = {startpos_id = "2134442793", faction_key = "cr_teb_order_of_the_blazing_sun", land_unit_key = "teb_colombo", starting_units = {"wh_dlc04_emp_cav_knights_blazing_sun_0","teb_republican_guard","teb_xbowmen","teb_pikemen"}}, 
        ["cataph_teb_catrazza_tow"] = {startpos_id = "1272167463", faction_key = "mixer_teb_catrazza", land_unit_key = "teb_catrazza", starting_units = {"teb_montante_greatswords","teb_carabiniers","teb_xbow_cav","teb_pikemen","teb_xbowmen","teb_paymaster","teb_galloper","wh3_main_ogr_inf_ironguts_0"}}, 
        ["cataph_teb_lucrezzia_tow"] = {startpos_id = "1146073257", faction_key = "cr_teb_pavona", land_unit_key = "teb_lucrezzia_belladonna", starting_units = {"teb_encarmine","teb_pavisiers","teb_paymaster","teb_half_pikes","teb_republican_guard"}}, 
        ["cataph_teb_cadavo_tow"] = {startpos_id = "1842456200", faction_key = "mixer_teb_new_world_colonies", land_unit_key = "teb_cadavo", starting_units = {"teb_light_cannon","teb_swash","teb_xbowmen","teb_pikemen","wh3_main_ogr_inf_maneaters_3","teb_conqui_riders"}}, 
    }
end 



core:add_ui_created_callback(
function(context)
if vfs.exists("script/frontend/mod/mixer_frontend.lua") then

-- enable modder factions
local factions_to_enable = {
                "1723362613",
                "102671841",
                "710497246",
                "2054831964",
                "674409277",
                "1609508987",
                "2041809480",
                "1325379312",--mind the comma

                "1905130419",
                "1386731579",
                "785415791",
                "1396713314",
                "1108245816",
                "2134442793",
                "1272167463",
                "1146073257",
                "1842456200",
              --TOWC  
                "235439226",
                "1695644591",
                "681801921"
}

for i = 1, #factions_to_enable do
mixer_enable_custom_faction(factions_to_enable[i])
end

for subtype in pairs(faction_leader_details) do
local startpos_id = faction_leader_details[subtype].startpos_id
local land_unit_key = faction_leader_details[subtype].land_unit_key
local faction_key = faction_leader_details[subtype].faction_key
local starting_units = faction_leader_details[subtype].starting_units



-- changes faction leader's name in frontend, use "" in land_units_key in faction_leader_details table (the thing above), if you don't wanna change it.
if land_unit_key ~= "" then
mixer_change_lord_name(startpos_id, land_unit_key)
end

-- changes faction leader's starting units in frontend, use "" in starting_units in faction leader details table, if you don't wanna change it.
if starting_units ~= "" then
mixer_add_starting_unit_list_for_faction(faction_key, starting_units)
end

-- adds them to the major faction list, so they are always visible.
mixer_add_faction_to_major_faction_list(faction_key)
end
end
end
)