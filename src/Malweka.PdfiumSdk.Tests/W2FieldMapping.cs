namespace Malweka.PdfiumSdk.Tests
{
    /// <summary>
    /// Field name mapping for IRS Form W-2
    /// Maps friendly field names to the actual XFA field paths in the PDF
    /// </summary>
    public static class W2FieldMapping
    {
        /// <summary>
        /// Dictionary mapping simple field names to complex W-2 PDF field paths
        /// These map to Copy A fields (for SSA submission)
        /// </summary>
        public static readonly Dictionary<string, string> CopyA = new Dictionary<string, string>
        {
            // Control fields
            ["void"] = "topmostSubform[0].CopyA[0].Void_ReadOrder[0].c1_1[0]",
            
            // Box a: Employee's Social Security Number
            ["employee_ssn"] = "topmostSubform[0].CopyA[0].BoxA_ReadOrder[0].f1_01[0]",
            
            // Box b: Employer Identification Number (EIN)
            ["employer_ein"] = "topmostSubform[0].CopyA[0].Col_Left[0].f1_02[0]",
            
            // Box c: Employer's name and address
            ["employer_name"] = "topmostSubform[0].CopyA[0].Col_Left[0].f1_03[0]",
            ["employer_address"] = "topmostSubform[0].CopyA[0].Col_Left[0].f1_04[0]",
            
            // Box e: Employee's first name and middle initial
            ["employee_first_name"] = "topmostSubform[0].CopyA[0].Col_Left[0].FirstName_ReadOrder[0].f1_05[0]",
            
            // Box f: Employee's last name
            ["employee_last_name"] = "topmostSubform[0].CopyA[0].Col_Left[0].LastName_ReadOrder[0].f1_06[0]",
            
            // Employee's address
            ["employee_address"] = "topmostSubform[0].CopyA[0].Col_Left[0].f1_07[0]",
            ["employee_city_state_zip"] = "topmostSubform[0].CopyA[0].Col_Left[0].f1_08[0]",
            
            // Box 1: Wages, tips, other compensation
            ["box1_wages"] = "topmostSubform[0].CopyA[0].Col_Right[0].Box1_ReadOrder[0].f1_09[0]",
            
            // Box 2: Federal income tax withheld
            ["box2_federal_tax"] = "topmostSubform[0].CopyA[0].Col_Right[0].f1_10[0]",
            
            // Box 3: Social security wages
            ["box3_ss_wages"] = "topmostSubform[0].CopyA[0].Col_Right[0].Box3_ReadOrder[0].f1_11[0]",
            
            // Box 4: Social security tax withheld
            ["box4_ss_tax"] = "topmostSubform[0].CopyA[0].Col_Right[0].f1_12[0]",
            
            // Box 5: Medicare wages and tips
            ["box5_medicare_wages"] = "topmostSubform[0].CopyA[0].Col_Right[0].Box5_ReadOrder[0].f1_13[0]",
            
            // Box 6: Medicare tax withheld
            ["box6_medicare_tax"] = "topmostSubform[0].CopyA[0].Col_Right[0].f1_14[0]",
            
            // Box 7: Social security tips
            ["box7_ss_tips"] = "topmostSubform[0].CopyA[0].Col_Right[0].Box7_ReadOrder[0].f1_15[0]",
            
            // Box 8: Allocated tips
            ["box8_allocated_tips"] = "topmostSubform[0].CopyA[0].Col_Right[0].f1_16[0]",
            
            // Box 9: (Deprecated - formerly advance EIC payment)
            ["box9_deprecated"] = "topmostSubform[0].CopyA[0].Col_Right[0].Box9_ReadOrder[0].f1_17[0]",
            
            // Box 10: Dependent care benefits
            ["box10_dependent_care"] = "topmostSubform[0].CopyA[0].Col_Right[0].f1_18[0]",
            
            // Box 11: Nonqualified plans
            ["box11_nonqualified_plans"] = "topmostSubform[0].CopyA[0].Col_Right[0].Box11_ReadOrder[0].f1_19[0]",
            
            // Box 12: Codes (supports up to 4 entries: a, b, c, d)
            ["box12a_code"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_20[0]",
            ["box12a_amount"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_21[0]",
            ["box12b_code"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_22[0]",
            ["box12b_amount"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_23[0]",
            ["box12c_code"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_24[0]",
            ["box12c_amount"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_25[0]",
            ["box12d_code"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_26[0]",
            ["box12d_amount"] = "topmostSubform[0].CopyA[0].Col_Right[0].Line12_ReadOrder[0].f1_27[0]",
            
            // Box 13: Checkboxes
            ["box13_statutory_employee"] = "topmostSubform[0].CopyA[0].Col_Right[0].Statutory_ReadOrder[0].c1_2[0]",
            ["box13_retirement_plan"] = "topmostSubform[0].CopyA[0].Col_Right[0].Retirement_ReadOrder[0].c1_3[0]",
            ["box13_third_party_sick_pay"] = "topmostSubform[0].CopyA[0].Col_Right[0].c1_4[0]",
            
            // Box 14: Other (employer use - can include union dues, health insurance, etc.)
            ["box14_other"] = "topmostSubform[0].CopyA[0].Col_Right[0].f1_28[0]",
            
            // Box 15: State, Employer's state ID number (supports 2 states)
            ["box15_state"] = "topmostSubform[0].CopyA[0].Boxes15_ReadOrder[0].Box15_ReadOrder[0].f1_29[0]",
            ["box15_state_id"] = "topmostSubform[0].CopyA[0].Boxes15_ReadOrder[0].f1_30[0]",
            ["box15_state_2"] = "topmostSubform[0].CopyA[0].Boxes15_ReadOrder[0].f1_31[0]",
            ["box15_state_id_2"] = "topmostSubform[0].CopyA[0].Boxes15_ReadOrder[0].f1_32[0]",
            
            // Box 16: State wages, tips, etc. (supports 2 states)
            ["box16_state_wages"] = "topmostSubform[0].CopyA[0].Box16_ReadOrder[0].f1_33[0]",
            ["box16_state_wages_2"] = "topmostSubform[0].CopyA[0].Box16_ReadOrder[0].f1_34[0]",
            
            // Box 17: State income tax (supports 2 states)
            ["box17_state_tax"] = "topmostSubform[0].CopyA[0].Box17_ReadOrder[0].f1_35[0]",
            ["box17_state_tax_2"] = "topmostSubform[0].CopyA[0].Box17_ReadOrder[0].f1_36[0]",
            
            // Box 18: Local wages, tips, etc. (supports 2 localities)
            ["box18_local_wages"] = "topmostSubform[0].CopyA[0].Box18_ReadOrder[0].f1_37[0]",
            ["box18_local_wages_2"] = "topmostSubform[0].CopyA[0].Box18_ReadOrder[0].f1_38[0]",
            
            // Box 19: Local income tax (supports 2 localities)
            ["box19_local_tax"] = "topmostSubform[0].CopyA[0].Box19_ReadOrder[0].f1_39[0]",
            ["box19_local_tax_2"] = "topmostSubform[0].CopyA[0].Box19_ReadOrder[0].f1_40[0]",
            
            // Box 20: Locality name (supports 2 localities)
            ["box20_locality"] = "topmostSubform[0].CopyA[0].f1_41[0]",
            ["box20_locality_2"] = "topmostSubform[0].CopyA[0].f1_42[0]"
        };

        /// <summary>
        /// Maps simple field names to Copy 1 (employee copy for state, city, or local tax department)
        /// Field structure is identical to Copy A but uses f2_ prefix instead of f1_
        /// </summary>
        public static readonly Dictionary<string, string> Copy1 = new Dictionary<string, string>
        {
            // Box a: Employee's Social Security Number
            ["employee_ssn"] = "topmostSubform[0].Copy1[0].BoxA_ReadOrder[0].f2_01[0]",
            
            // Box b: Employer Identification Number (EIN)
            ["employer_ein"] = "topmostSubform[0].Copy1[0].Col_Left[0].f2_02[0]",
            
            // Box c: Employer's name and address
            ["employer_name"] = "topmostSubform[0].Copy1[0].Col_Left[0].f2_03[0]",
            ["employer_address"] = "topmostSubform[0].Copy1[0].Col_Left[0].f2_04[0]",
            
            // Box e: Employee's first name and middle initial
            ["employee_first_name"] = "topmostSubform[0].Copy1[0].Col_Left[0].FirstName_ReadOrder[0].f2_05[0]",
            
            // Box f: Employee's last name
            ["employee_last_name"] = "topmostSubform[0].Copy1[0].Col_Left[0].LastName_ReadOrder[0].f2_06[0]",
            
            // Employee's address
            ["employee_address"] = "topmostSubform[0].Copy1[0].Col_Left[0].f2_07[0]",
            ["employee_city_state_zip"] = "topmostSubform[0].Copy1[0].Col_Left[0].f2_08[0]",
            
            // Boxes 1-20 (using same structure as Copy A)
            ["box1_wages"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box1_ReadOrder[0].f2_09[0]",
            ["box2_federal_tax"] = "topmostSubform[0].Copy1[0].Col_Right[0].f2_10[0]",
            ["box3_ss_wages"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box3_ReadOrder[0].f2_11[0]",
            ["box4_ss_tax"] = "topmostSubform[0].Copy1[0].Col_Right[0].f2_12[0]",
            ["box5_medicare_wages"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box5_ReadOrder[0].f2_13[0]",
            ["box6_medicare_tax"] = "topmostSubform[0].Copy1[0].Col_Right[0].f2_14[0]",
            ["box7_ss_tips"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box7_ReadOrder[0].f2_15[0]",
            ["box8_allocated_tips"] = "topmostSubform[0].Copy1[0].Col_Right[0].f2_16[0]",
            ["box9_deprecated"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box9_ReadOrder[0].f2_17[0]",
            ["box10_dependent_care"] = "topmostSubform[0].Copy1[0].Col_Right[0].f2_18[0]",
            ["box11_nonqualified_plans"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box11__ReadOrder[0].f2_19[0]",
            
            // Box 12
            ["box12a_code"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_20[0]",
            ["box12a_amount"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_21[0]",
            ["box12b_code"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_22[0]",
            ["box12b_amount"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_23[0]",
            ["box12c_code"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_24[0]",
            ["box12c_amount"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_25[0]",
            ["box12d_code"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_26[0]",
            ["box12d_amount"] = "topmostSubform[0].Copy1[0].Col_Right[0].Box12_ReadOrder[0].f2_27[0]",
            
            // Box 13
            ["box13_statutory_employee"] = "topmostSubform[0].Copy1[0].Col_Right[0].Statutory_ReadOrder[0].c2_2[0]",
            ["box13_retirement_plan"] = "topmostSubform[0].Copy1[0].Col_Right[0].Retirement_ReadOrder[0].c2_3[0]",
            ["box13_third_party_sick_pay"] = "topmostSubform[0].Copy1[0].Col_Right[0].c2_4[0]",
            
            // Boxes 14-20
            ["box14_other"] = "topmostSubform[0].Copy1[0].Col_Right[0].f2_28[0]",
            ["box15_state"] = "topmostSubform[0].Copy1[0].Boxes15_ReadOrder[0].Box15_ReadOrder[0].f2_29[0]",
            ["box15_state_id"] = "topmostSubform[0].Copy1[0].Boxes15_ReadOrder[0].f2_30[0]",
            ["box15_state_2"] = "topmostSubform[0].Copy1[0].Boxes15_ReadOrder[0].f2_31[0]",
            ["box15_state_id_2"] = "topmostSubform[0].Copy1[0].Boxes15_ReadOrder[0].f2_32[0]",
            ["box16_state_wages"] = "topmostSubform[0].Copy1[0].Box16_ReadOrder[0].f2_33[0]",
            ["box16_state_wages_2"] = "topmostSubform[0].Copy1[0].Box16_ReadOrder[0].f2_34[0]",
            ["box17_state_tax"] = "topmostSubform[0].Copy1[0].Box17_ReadOrder[0].f2_35[0]",
            ["box17_state_tax_2"] = "topmostSubform[0].Copy1[0].Box17_ReadOrder[0].f2_36[0]",
            ["box18_local_wages"] = "topmostSubform[0].Copy1[0].Box18_ReadOrder[0].f2_37[0]",
            ["box18_local_wages_2"] = "topmostSubform[0].Copy1[0].Box18_ReadOrder[0].f2_38[0]",
            ["box19_local_tax"] = "topmostSubform[0].Copy1[0].Box19_ReadOrder[0].f2_39[0]",
            ["box19_local_tax_2"] = "topmostSubform[0].Copy1[0].Box19_ReadOrder[0].f2_40[0]",
            ["box20_locality"] = "topmostSubform[0].Copy1[0].f2_41[0]",
            ["box20_locality_2"] = "topmostSubform[0].Copy1[0].f2_42[0]"
        };

        /// <summary>
        /// Gets the field path for a specific copy type
        /// </summary>
        /// <param name="copyType">Copy type (e.g., "CopyA", "Copy1", "CopyB", "CopyC", "Copy2")</param>
        /// <param name="friendlyName">Friendly field name (e.g., "employee_first_name")</param>
        /// <returns>Full PDF field path</returns>
        public static string GetFieldPath(string copyType, string friendlyName)
        {
            var mapping = copyType switch
            {
                "CopyA" => CopyA,
                "Copy1" => Copy1,
                // CopyB, CopyC, and Copy2 follow the same pattern as Copy1
                _ => throw new System.ArgumentException($"Unknown copy type: {copyType}")
            };

            if (!mapping.TryGetValue(friendlyName, out var fieldPath))
            {
                throw new System.ArgumentException($"Unknown field name: {friendlyName}");
            }

            return fieldPath;
        }

        /// <summary>
        /// W-2 Copy Types and their purposes
        /// </summary>
        public static class CopyTypes
        {
            public const string CopyA = "CopyA";     // For Social Security Administration
            public const string Copy1 = "Copy1";     // For State, City, or Local Tax Department
            public const string CopyB = "CopyB";     // To Be Filed With Employee's FEDERAL Tax Return
            public const string CopyC = "CopyC";     // For EMPLOYEE'S RECORDS
            public const string Copy2 = "Copy2";     // To Be Filed With Employee's State, City, or Local Income Tax Return
        }
    }
}