#include <BRep_Builder.hxx>
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
#include <TopoDS_Compound.hxx>
#include <TopoDS_Face.hxx>
#include <TopTools_MapOfShape.hxx>
#include <UnitsMethods_LengthUnit.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_GraphNode.hxx>
#include <XCAFDoc_LayerTool.hxx>
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

Handle(TDocStd_Document) ReadDocument(const fs::path& path, bool readVisibility)
{
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error("Cannot open the STEP file.");
    STEPCAFControl_Reader reader;
    reader.SetLayerMode(readVisibility);
    if (readVisibility) reader.SetSHUOMode(true);
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

class Visibility
{
public:
    Visibility(const Handle(TDocStd_Document)& document, bool enabled)
    {
        if (enabled)
        {
            colors = XCAFDoc_DocumentTool::ColorTool(document->Main());
            layers = XCAFDoc_DocumentTool::LayerTool(document->Main());
        }
    }

    bool Enabled() const { return !colors.IsNull(); }

    bool Hidden(const TDF_Label& label) const
    {
        if (!Enabled()) return false;
        if (!colors->IsVisible(label)) return true;
        TDF_LabelSequence assigned;
        layers->GetLayers(label, assigned);
        return !assigned.IsEmpty() && std::none_of(assigned.begin(), assigned.end(),
            [this](const TDF_Label& layer) { return layers->IsVisible(layer); });
    }

    bool HiddenUsage(const TDF_LabelSequence& path) const
    {
        if (path.Length() < 2) return false;
        TDF_LabelSequence suffix;
        suffix.Append(path.Last());
        for (int index = path.Length() - 1; index >= 1; --index)
        {
            suffix.Prepend(path.Value(index));
            Handle(XCAFDoc_GraphNode) usage;
            if (XCAFDoc_ShapeTool::FindSHUO(suffix, usage) && !colors->IsVisible(usage->Label())) return true;
        }
        return false;
    }

private:
    Handle(XCAFDoc_ColorTool) colors;
    Handle(XCAFDoc_LayerTool) layers;
};

class ProductTree
{
public:
    ProductTree(const Handle(TDocStd_Document)& document, const Visibility& visibility) : visibility(visibility)
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
    const Visibility& visibility;
    TDF_LabelSequence roots;
    TDF_LabelSequence componentPath;
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
        const bool trackUsage = xml && visibility.Enabled() && depth > 0;
        if (trackUsage) componentPath.Append(occurrence);
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
            if (visibility.Hidden(occurrence) || (occurrence != definition && visibility.Hidden(definition)) ||
                visibility.HiddenUsage(componentPath)) *xml << " hidden=\"1\"";
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
        if (trackUsage) componentPath.Remove(componentPath.Length());
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

TopoDS_Shape VisibleGeometry(const TopoDS_Shape& shape, const TDF_Label& definition, const Visibility& visibility)
{
    const auto localShape = shape.Located(TopLoc_Location());
    if (!visibility.Enabled()) return localShape;

    TopTools_MapOfShape hiddenFaces;
    TDF_LabelSequence labels;
    XCAFDoc_ShapeTool::GetSubShapes(definition, labels);
    const auto toLocal = shape.Location().Inverted();
    for (const auto& label : labels)
    {
        if (!visibility.Hidden(label)) continue;
        const auto subshape = XCAFDoc_ShapeTool::GetShape(label);
        if (subshape.IsNull()) continue;
        for (TopExp_Explorer faces(subshape.Moved(toLocal), TopAbs_FACE); faces.More(); faces.Next())
            hiddenFaces.Add(faces.Current());
    }
    if (hiddenFaces.IsEmpty()) return localShape;

    BRep_Builder builder;
    TopoDS_Compound result;
    builder.MakeCompound(result);
    for (TopExp_Explorer faces(localShape, TopAbs_FACE); faces.More(); faces.Next())
        if (!hiddenFaces.Contains(faces.Current())) builder.Add(result, faces.Current());
    if (result.NbChildren() == 0) throw std::runtime_error("All faces of the STEP part are hidden.");
    return result;
}

MeshCoverage WriteMesh(const fs::path& file, const TDF_Label& definition, const Handle(XCAFDoc_ColorTool)& colors,
                       const Visibility& visibility)
{
    const auto shape = VisibleGeometry(XCAFDoc_ShapeTool::GetShape(definition), definition, visibility);
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
                 const fs::path& selection, const fs::path& folder, const Visibility& visibility)
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
            const auto coverage = WriteMesh(pending, item->second, colors, visibility);
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
        const bool readTree = command == L"tree" || command == L"visible-tree";
        const bool readMeshes = command == L"mesh" || command == L"visible-mesh";
        if (!(readTree && argc == 4) && !(readMeshes && argc == 5))
            throw std::runtime_error("Expected [visible-]tree <STEP> <XML> or [visible-]mesh <STEP> <selection> <folder>.");
        const bool readVisibility = command == L"visible-tree" || command == L"visible-mesh";
        const auto document = ReadDocument(argv[2], readVisibility);
        const Visibility visibility(document, readVisibility);
        ProductTree tree(document, visibility);
        if (readTree)
        {
            std::ofstream output(fs::path(argv[3]), std::ios::binary);
            output.exceptions(std::ios::badbit | std::ios::failbit);
            tree.Write(&output);
            output.close();
        }
        else WriteMeshes(document, tree, argv[3], argv[4], visibility);
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
