#include <BRepLib_ToolTriangulatedShape.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRep_Tool.hxx>
#include <Poly_Triangulation.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPConstruct_ExternRefs.hxx>
#include <Standard_Failure.hxx>
#include <TDF_Tool.hxx>
#include <TDataStd_Name.hxx>
#include <TDocStd_Document.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <UnitsMethods_LengthUnit.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
namespace fs = std::filesystem;
using Definitions = std::map<std::string, TDF_Label>;

std::string DefinitionId(const TDF_Label& label)
{
    TCollection_AsciiString entry;
    TDF_Tool::Entry(label, entry);
    std::string id(entry.ToCString());
    std::replace(id.begin(), id.end(), ':', '_');
    return id;
}

std::string Name(const TDF_Label& label)
{
    Handle(TDataStd_Name) attribute;
    if (!label.FindAttribute(TDataStd_Name::GetID(), attribute))
        return {};
    const auto& name = attribute->Get();
    std::vector<char> buffer(name.LengthOfCString() + 1);
    char* destination = buffer.data();
    name.ToUTF8CString(destination);
    return buffer.data();
}

std::string Xml(const std::string& value)
{
    std::string escaped;
    for (char c : value)
    {
        switch (c)
        {
        case '&': escaped += "&amp;"; break;
        case '<': escaped += "&lt;"; break;
        case '>': escaped += "&gt;"; break;
        case '"': escaped += "&quot;"; break;
        default:
            if (static_cast<unsigned char>(c) >= 32) escaped += c;
        }
    }
    return escaped;
}

Handle(TDocStd_Document) ReadDocument(const fs::path& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error("Cannot open the STEP file.");
    STEPCAFControl_Reader reader;
    reader.SetLayerMode(false);
    reader.SetPropsMode(false);
    if (reader.ReadStream("source.step", input) != IFSelect_RetDone)
        throw std::runtime_error("Invalid or unsupported STEP file.");

    STEPConstruct_ExternRefs references(reader.Reader().WS());
    references.LoadExternRefs();
    for (int index = 1; index <= references.NbExternRefs(); ++index)
    {
        const char* file = references.FileName(index);
        if (file && *file)
            throw std::runtime_error("External STEP links require a self-contained STEP export.");
    }

    Handle(TDocStd_Document) document = new TDocStd_Document("BinXCAF");
    XCAFDoc_DocumentTool::SetLengthUnit(document, 1.0, UnitsMethods_LengthUnit_Millimeter);
    XCAFDoc_ShapeTool::SetAutoNaming(false);
    if (!reader.Transfer(document)) throw std::runtime_error("STEP shape transfer failed.");
    return document;
}

class ProductTree
{
public:
    explicit ProductTree(const Handle(TDocStd_Document)& document)
    {
        XCAFDoc_DocumentTool::ShapeTool(document->Main())->GetFreeShapes(roots);
        if (roots.IsEmpty()) throw std::runtime_error("The STEP file contains no products.");
    }

    Definitions Write(std::ostream* xml)
    {
        Definitions definitions;
        if (xml) *xml << "<?xml version=\"1.0\" encoding=\"UTF-8\"?><step version=\"1\">\n"
                      << std::setprecision(std::numeric_limits<double>::max_digits10);
        for (const auto& root : roots) Visit(root, xml, definitions, 0);
        if (xml) *xml << "</step>\n";
        return definitions;
    }

private:
    TDF_LabelSequence roots;
    int occurrences = 0;

    void Visit(const TDF_Label& occurrence, std::ostream* xml, Definitions& definitions, int depth)
    {
        if (depth > 100 || ++occurrences > 100000)
            throw std::runtime_error("The STEP assembly exceeds the supported nesting or occurrence count.");
        TDF_Label definition;
        if (!XCAFDoc_ShapeTool::GetReferredShape(occurrence, definition)) definition = occurrence;
        const bool assembly = XCAFDoc_ShapeTool::IsAssembly(definition);
        const auto id = DefinitionId(definition);
        if (!assembly) definitions.emplace(id, definition);
        if (xml)
        {
            auto definitionName = Name(definition);
            if (definitionName.empty()) definitionName = (assembly ? "Assembly " : "Part ") + id;
            *xml << (assembly ? "<assembly" : "<part") << " name=\"" << Xml(Name(occurrence))
                 << "\" definitionName=\"" << Xml(definitionName) << "\" frame=\"";
            const auto transform = XCAFDoc_ShapeTool::GetLocation(occurrence).Transformation();
            for (int col = 1; col <= 4; ++col)
                for (int row = 1; row <= 3; ++row)
                    *xml << transform.Value(row, col) << (col == 4 && row == 3 ? "\"" : " ");
            if (!assembly)
            {
                const auto shape = XCAFDoc_ShapeTool::GetShape(definition);
                const bool hasFaces = !shape.IsNull() && TopExp_Explorer(shape, TopAbs_FACE).More();
                *xml << " definition=\"" << id << "\" mesh=\"" << (hasFaces ? 1 : 0) << "\"/>\n";
            }
            else *xml << ">\n";
        }
        if (assembly)
        {
            TDF_LabelSequence components;
            XCAFDoc_ShapeTool::GetComponents(definition, components);
            for (const auto& component : components) Visit(component, xml, definitions, depth + 1);
            if (xml) *xml << "</assembly>\n";
        }
    }
};

template<class T> void Put(std::ostream& output, T value)
{
    output.write(reinterpret_cast<const char*>(&value), sizeof(T));
}

std::array<std::uint8_t, 3> ToRgb(const Quantity_Color& color)
{
    double r, g, b;
    color.Values(r, g, b, Quantity_TOC_sRGB);
    return { static_cast<std::uint8_t>(std::lround(255 * r)), static_cast<std::uint8_t>(std::lround(255 * g)),
             static_cast<std::uint8_t>(std::lround(255 * b)) };
}

bool WriteFace(std::ostream& output, const TopoDS_Face& face, const std::array<std::uint8_t, 3>& color)
{
    TopLoc_Location location;
    const auto& mesh = BRep_Tool::Triangulation(face, location);
    if (mesh.IsNull() || mesh->NbNodes() < 3 || mesh->NbTriangles() == 0) return false;
    BRepLib_ToolTriangulatedShape::ComputeNormals(face, mesh);
    const auto transform = location.Transformation();
    const bool reversed = face.Orientation() == TopAbs_REVERSED;
    const bool reverseWinding = reversed != transform.IsNegative();
    std::vector<gp_Pnt> points;
    points.reserve(mesh->NbNodes());
    for (int i = 1; i <= mesh->NbNodes(); ++i)
        points.push_back(mesh->Node(i).Transformed(transform));
    std::vector<std::array<std::int32_t, 3>> triangles;
    triangles.reserve(mesh->NbTriangles());
    for (int i = 1; i <= mesh->NbTriangles(); ++i)
    {
        int a, b, c;
        mesh->Triangle(i).Get(a, b, c);
        if (reverseWinding) std::swap(b, c);
        const auto& p = points.at(a - 1);
        if (gp_Vec(p, points.at(b - 1)).Crossed(gp_Vec(p, points.at(c - 1))).SquareMagnitude() > 0)
            triangles.push_back({a - 1, b - 1, c - 1});
    }
    if (triangles.empty()) return false;
    Put(output, std::int32_t(points.size()));
    Put(output, std::int32_t(triangles.size()));
    for (auto component : color) Put(output, component);
    for (int i = 1; i <= mesh->NbNodes(); ++i)
    {
        const auto& point = points[i - 1];
        Put(output, point.X()); Put(output, point.Y()); Put(output, point.Z());
        if (mesh->HasNormals())
        {
            auto normal = mesh->Normal(i).Transformed(transform);
            if (reversed) normal.Reverse();
            Put(output, normal.X()); Put(output, normal.Y()); Put(output, normal.Z());
        }
        else
        {
            Put(output, 0.0); Put(output, 0.0); Put(output, 0.0);
        }
    }
    for (const auto& triangle : triangles)
        for (auto index : triangle) Put(output, index);
    return true;
}

struct MeshCoverage
{
    int written = 0;
    int missing = 0;
};

MeshCoverage WriteMesh(const fs::path& file, const TDF_Label& definition, const Handle(XCAFDoc_ColorTool)& colors)
{
    const auto shape = XCAFDoc_ShapeTool::GetShape(definition).Located(TopLoc_Location());
    // Absolute positional deflection is in millimeters; angular deflection is radians.
    BRepMesh_IncrementalMesh mesher(shape, 0.05, false, 0.20, true);
    Quantity_Color base(0.75, 0.75, 0.75, Quantity_TOC_sRGB);
    if (!colors->GetColor(definition, XCAFDoc_ColorSurf, base))
        colors->GetColor(definition, XCAFDoc_ColorGen, base);
    std::ofstream output(file, std::ios::binary);
    output.exceptions(std::ios::badbit | std::ios::failbit);
    output.write("TXSTPM01", 8);
    MeshCoverage coverage;
    for (TopExp_Explorer explorer(shape, TopAbs_FACE); explorer.More(); explorer.Next())
    {
        const auto& face = TopoDS::Face(explorer.Current());
        auto color = base;
        if (!colors->GetColor(face, XCAFDoc_ColorSurf, color))
            colors->GetColor(face, XCAFDoc_ColorGen, color);
        if (WriteFace(output, face, ToRgb(color))) ++coverage.written;
        else ++coverage.missing;
    }
    if (coverage.written == 0) throw std::runtime_error("The STEP part contains no drawable triangles.");
    Put(output, std::int32_t(0));
    output.close();
    return coverage;
}

void WriteMeshes(const Handle(TDocStd_Document)& document, ProductTree& tree,
                 const fs::path& selection, const fs::path& folder)
{
    const auto definitions = tree.Write(nullptr);
    const auto colors = XCAFDoc_DocumentTool::ColorTool(document->Main());
    std::ifstream list(selection);
    if (!list) throw std::runtime_error("Cannot read the selected STEP definitions.");
    for (std::string id; std::getline(list, id);)
    {
        const auto item = definitions.find(id);
        if (item == definitions.end()) throw std::runtime_error("A selected STEP definition is no longer available.");
        const auto pending = folder / (id + ".pending");
        std::string error;
        try
        {
            const auto coverage = WriteMesh(pending, item->second, colors);
            if (coverage.missing != 0)
            {
                std::ofstream warning(folder / (id + ".warning"), std::ios::binary);
                warning.exceptions(std::ios::badbit | std::ios::failbit);
                warning << coverage.written << ' ' << coverage.missing;
                warning.close();
            }
            fs::rename(pending, folder / (id + ".mesh"));
            continue;
        }
        catch (const Standard_Failure& failure)
        {
            const char* message = failure.GetMessageString();
            error = message ? message : "STEP part tessellation failed.";
        }
        catch (const std::exception& failure) { error = failure.what(); }
        fs::remove(pending);
        std::ofstream diagnostic(folder / (id + ".error"), std::ios::binary);
        diagnostic << error;
    }
}
}

int wmain(int argc, wchar_t* argv[])
{
    try
    {
        const std::wstring command = argc > 1 ? argv[1] : L"";
        if (!(command == L"tree" && argc == 4) && !(command == L"mesh" && argc == 5))
            throw std::runtime_error("Expected tree <STEP> <XML> or mesh <STEP> <selection> <folder>.");
        const auto document = ReadDocument(argv[2]);
        ProductTree tree(document);
        if (command == L"tree")
        {
            std::ofstream output(fs::path(argv[3]), std::ios::binary);
            output.exceptions(std::ios::badbit | std::ios::failbit);
            tree.Write(&output);
            output.close();
        }
        else WriteMeshes(document, tree, argv[3], argv[4]);
        return 0;
    }
    catch (const Standard_Failure& error)
    {
        const char* message = error.GetMessageString();
        std::cerr << (message ? message : "STEP processing failed.") << '\n';
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; }
    return 1;
}
